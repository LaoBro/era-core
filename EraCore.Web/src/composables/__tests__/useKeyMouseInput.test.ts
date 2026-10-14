// @vitest-environment happy-dom
import { describe, it, expect, vi, afterEach } from 'vitest';
import { createApp, h, type App } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import { isTypingTarget, useKeyMouseInput } from '../useKeyMouseInput';
import { useConnectionStore } from '../../stores/connection';

/**
 * `useKeyMouseInput` 里唯一可单测的纯函数：`isTypingTarget`。
 *
 * （监听器本体依赖 window/document 与 connection store，留给集成层；这里只钉住
 * 输入框守卫的判定 —— 它决定了「玩家打字会不会被误当成游戏内按键」。）
 *
 * spec-v3 M2b：玩家在 InputBar 打字时不该让 `GETKEY(0x41)` 变成 1。
 */
describe('isTypingTarget——焦点是否落在可输入元素', () => {
  it('输入框 / 文本域 / 下拉 → true', () => {
    expect(isTypingTarget(document.createElement('input'))).toBe(true);
    expect(isTypingTarget(document.createElement('textarea'))).toBe(true);
    expect(isTypingTarget(document.createElement('select'))).toBe(true);
  });

  it('contentEditable → true', () => {
    const div = document.createElement('div');
    div.contentEditable = 'true';
    expect(isTypingTarget(div)).toBe(true);
  });

  it('普通元素 → false（含 button —— 焦点在按钮上不算打字）', () => {
    expect(isTypingTarget(document.createElement('div'))).toBe(false);
    expect(isTypingTarget(document.createElement('button'))).toBe(false);
    expect(isTypingTarget(document.createElement('span'))).toBe(false);
  });

  it('null（无 activeElement）→ false（不抛）', () => {
    expect(isTypingTarget(null)).toBe(false);
  });

  it('🔒 标签名大小写无关（HTML 里 createElement 返回大写 tagName）', () => {
    const el = document.createElement('input');
    expect(el.tagName).toBe('INPUT');
    expect(isTypingTarget(el)).toBe(true);
  });
});

/**
 * M6：焦点上报（`ISACTIVE` 的真值）与失焦补发 up。
 *
 * 监听器本体依赖 window/document 与 connection store ⇒ 这里挂一个真组件把
 * `useKeyMouseInput` 跑起来，再派发真实 DOM 事件断言上报内容。
 */
describe('useKeyMouseInput：焦点上报（M6）', () => {
  let app: App | null = null;

  /** 挂一个只调用本 composable 的宿主组件，并 spy 掉 store 的上报出口。 */
  function mountHook(enabled?: () => boolean) {
    const pinia = createPinia();
    setActivePinia(pinia);
    const conn = useConnectionStore();
    conn.reportInputState = vi.fn();
    const host = document.createElement('div');
    app = createApp({
      setup() {
        useKeyMouseInput(enabled);
        return () => h('div');
      },
    });
    app.use(pinia);
    app.mount(host);
    return conn;
  }

  afterEach(() => {
    app?.unmount();
    app = null;
  });

  it('blur ⇒ 上报 focus:false（即便没有按下的键 —— 旧实现会因 pressed 为空整条吞掉）', () => {
    const conn = mountHook();

    window.dispatchEvent(new Event('blur'));

    expect(conn.reportInputState).toHaveBeenCalledWith({ focus: false });
  });

  it('focus ⇒ 上报 focus:true', () => {
    const conn = mountHook();

    window.dispatchEvent(new Event('focus'));

    expect(conn.reportInputState).toHaveBeenCalledWith({ focus: true });
  });

  it('🔒 blur 同时补发全量 up（浏览器失焦后不会再送 keyup）', () => {
    const conn = mountHook();

    window.dispatchEvent(new KeyboardEvent('keydown', { code: 'KeyA' }));
    window.dispatchEvent(new Event('blur'));

    expect(conn.reportInputState).toHaveBeenCalledWith({
      keys: [{ code: 0x41, down: false }],
      focus: false,
    });
    // blur 只清状态、不拆监听：之后的按下照常上报（表已清，但通道必须还在）
    vi.mocked(conn.reportInputState).mockClear();
    window.dispatchEvent(new KeyboardEvent('keydown', { code: 'KeyA' }));
    expect(conn.reportInputState).toHaveBeenCalledWith({ keys: [{ code: 0x41, down: true }] });
  });

  it('标签页隐藏 ⇒ 视作失焦（部分 WebView 切标签不派发 blur）', () => {
    const conn = mountHook();
    const spy = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden');

    document.dispatchEvent(new Event('visibilitychange'));

    expect(conn.reportInputState).toHaveBeenCalledWith({ focus: false });
    spy.mockRestore();
  });

  it('enabled=false 时一律不上报（未连接 / 旁观）', () => {
    const conn = mountHook(() => false);

    window.dispatchEvent(new Event('blur'));
    window.dispatchEvent(new Event('focus'));

    expect(conn.reportInputState).not.toHaveBeenCalled();
  });
});
