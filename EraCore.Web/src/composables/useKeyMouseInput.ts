/**
 * 真实键鼠事件 → 统一输入宿主上报（ADR-0031 / spec-v3 **M2b**）。
 *
 * ## 职责
 *
 * M2 把引擎侧通道（`GETKEY` / `GETKEYTRIGGERED` → `IInputHost`）和协议捎带字段都建好了，
 * 但**没有输入源** —— `DefaultInputHost` 一直空着。本模块把浏览器里真实发生的
 * keydown / keyup / mousedown / mouseup 接到那条通道上。
 *
 * ## 四条硬性约束（都是回退 E-min 后重审出来的）
 *
 * 1. **逐条立即上报，不做时间合并**（§3.6）
 *    down+up 若被合并进同一帧，宿主按序处理后电平已归零 ⇒ 按键被压成零宽度脉冲
 *    ⇒ 快速按键的触发整条丢失。人类按键只有 ~10 次/秒，没有任何合并的必要。
 * 2. **down / up 必须保序**（§3.6 约束二）
 *    `reportInputState` 内部走串行队列，故这里只需保证调用顺序正确。
 * 3. **不过 `inputInFlight` 锁**（§3.6 约束一）
 *    挡掉一条 mousedown ⇒ 电平永不置位 ⇒ 触发全丢；长定时等待（`AWAIT 8000`）
 *    期间没有帧来解锁，还会让电平卡住整段等待。
 * 4. **不参与 E-min / 悬停补偿**（ADR-0031 决策 5）
 *    只上报**真实**事件。`hoverValue` 等 M5 打通真悬停后再接。
 *
 * ## 输入框守卫
 *
 * 玩家在 InputBar 打字时不该让 `GETKEY(0x41)` 变成 1 —— 那会让正在跑的脚本
 * 误以为玩家按了 A。守卫规则见 `isTypingTarget`。
 *
 * ⚠️ **配对优先**：若某个键已经上报过 down，即使松手时焦点已切到输入框，
 * 也必须上报 up —— 否则电平永久卡住。
 */
import { onBeforeUnmount, onMounted } from 'vue';
import { useConnectionStore } from '../stores/connection';
import { virtualKeyFromCode, virtualKeyFromMouseButton } from '../lib/keyCodes';

/** 焦点是否落在可输入元素上（InputBar / 设置项 / contentEditable）。 */
export function isTypingTarget(el: Element | null): boolean {
  if (!el) return false;
  const tag = el.tagName;
  if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return true;
  return (el as HTMLElement).isContentEditable === true;
}

/**
 * 安装 / 卸载全局键鼠监听。在组件 `setup` 内调用（通常是 App.vue）。
 *
 * @param enabled 返回 false 时不上报（未连接 / 旁观中等）。省略视为总是上报
 *                —— 服务端侧还有一层 gate，前端不必重复判断。
 */
export function useKeyMouseInput(enabled?: () => boolean): void {
  /**
   * 已上报过 down、尚未收到 up 的键。
   *
   * 两个用途：
   * - 窗口失焦时浏览器会吞掉后续 keyup/mouseup，靠这张表补发全量 up；
   * - 配对优先（见文件头的守卫说明）。
   */
  const pressed = new Set<number>();

  function report(code: number, down: boolean): void {
    if (down) pressed.add(code);
    else pressed.delete(code);
    useConnectionStore().reportInputState({ keys: [{ code, down }] });
  }

  function onKeyDown(e: KeyboardEvent): void {
    // 长按自动重复：重复 down 本身无害（版本号只在 false→true 递增），但纯属浪费帧
    if (e.repeat) return;
    // 打字中不上报 —— 输入框有自己的提交语义，不该污染 GETKEY
    if (isTypingTarget(document.activeElement)) return;
    const vk = virtualKeyFromCode(e.code);
    if (vk !== null) report(vk, true);
  }

  function onKeyUp(e: KeyboardEvent): void {
    const vk = virtualKeyFromCode(e.code);
    if (vk === null) return;
    // 配对优先：只要之前上报过 down，就必须上报 up，无论焦点在哪
    if (!pressed.has(vk) && isTypingTarget(document.activeElement)) return;
    report(vk, false);
  }

  function onMouseDown(e: MouseEvent): void {
    // 鼠标不受输入框守卫约束 —— 点按钮本身就是一次真实点击，按钮有自己的 @click 处理
    const vk = virtualKeyFromMouseButton(e.button);
    if (vk !== null) report(vk, true);
  }

  function onMouseUp(e: MouseEvent): void {
    const vk = virtualKeyFromMouseButton(e.button);
    if (vk !== null) report(vk, false);
  }

  /**
   * 窗口失焦兜底：切标签页 / 点到浏览器外再松手时，浏览器不会派发后续的
   * keyup / mouseup。若不补发，电平会永久停在 1 ⇒ 幽灵输入。
   */
  function releaseAll(): void {
    if (pressed.size === 0) return;
    const keys = [...pressed].map(code => ({ code, down: false }));
    pressed.clear();
    useConnectionStore().reportInputState({ keys });
  }

  function shouldReport(): boolean {
    return enabled ? enabled() : true;
  }

  // 包一层：enabled=false 时完全不动（但仍从 pressed 里清掉，避免状态残留）
  function guard<T extends (e: never) => void>(fn: T) {
    return (e: never): void => {
      if (!shouldReport()) return;
      fn(e);
    };
  }

  const boundKeyDown = guard(onKeyDown as (e: never) => void);
  const boundKeyUp = guard(onKeyUp as (e: never) => void);
  const boundMouseDown = guard(onMouseDown as (e: never) => void);
  const boundMouseUp = guard(onMouseUp as (e: never) => void);

  onMounted(() => {
    window.addEventListener('keydown', boundKeyDown as EventListener);
    window.addEventListener('keyup', boundKeyUp as EventListener);
    window.addEventListener('mousedown', boundMouseDown as EventListener);
    // mouseup 挂在 window 而非某个元素：玩家可能拖到元素外才松手
    window.addEventListener('mouseup', boundMouseUp as EventListener);
    window.addEventListener('blur', releaseAll);
    document.addEventListener('visibilitychange', onVisibilityChange);
  });

  function onVisibilityChange(): void {
    if (document.visibilityState === 'hidden') releaseAll();
  }

  onBeforeUnmount(() => {
    window.removeEventListener('keydown', boundKeyDown as EventListener);
    window.removeEventListener('keyup', boundKeyUp as EventListener);
    window.removeEventListener('mousedown', boundMouseDown as EventListener);
    window.removeEventListener('mouseup', boundMouseUp as EventListener);
    window.removeEventListener('blur', releaseAll);
    document.removeEventListener('visibilitychange', onVisibilityChange);
    pressed.clear();
  });
}
