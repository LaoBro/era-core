<script setup lang="ts">
/**
 * DrawerMenu.vue — 游戏页「更多」菜单的右侧抽屉实现（替换原 PopupMenu 浮层）。
 * 触发点：游戏画面右上角 ⋮ 固定控件。顶部左对齐游戏标题，无关闭按钮；
 * 关闭方式：菜单项点击 / 点抽屉外 scrim / 右滑收起 / Escape（MAUI 物理返回键由 App.vue 统一处理）。
 * 样式沿用 ui-redesign 统一令牌：--color-surface-raised、--radius-surface、--elevation-menu；
 * 出入场为横向 slide，动效走 --motion-slow / --motion-fast。
 *
 * 常驻渲染：本组件**不随开关挂卸载**，由 visible prop + .is-open 类驱动。收起态 opacity:0
 * + inert 保留合成层；空闲期做一次「唤醒绘制」（is-warming + opacity 0.01，面板归位），
 * 让真实面板的 tile 与 shader 提前栅格化/编译并存留，从而消掉首次打开的③（真实面板
 * 栅格化）与①（shader 编译）。收起态不能 display:none / visibility:hidden / scale(0)——
 * 那会打掉合成层与 tile。见 .scratch/menu-first-open-jank/spec.md。
 */
import { ref, watch, onMounted, onUnmounted, nextTick } from 'vue';
import type { PopupMenuItem } from './menuTypes';

const props = defineProps<{
  items: PopupMenuItem[];
  /** 抽屉头部左对齐标题——游戏显示名（game.lastPlayedGame）。 */
  title: string;
  /** 打开态。常驻渲染：关闭不光卸载、仅 opacity:0 + inert 隐藏（保住合成层与 tile）。 */
  visible: boolean;
}>();

const emit = defineEmits<{
  (e: 'close'): void;
}>();

const rootEl = ref<HTMLElement | null>(null);

/** 菜单打开前获得焦点的元素——关闭时归还焦点（沿用 PopupMenu 的最小焦点管理）。 */
let previouslyFocused: HTMLElement | null = null;

/* ---------- 空闲期唤醒绘制（真实面板一次，进程只做一次） ---------- */
const warming = ref(false); // 挂 .is-warming：真实面板以 opacity 0.01 归位绘制，完成后收回
let warmed = false;

/** 页面空闲后把真实抽屉面板画一次（非零透明度→真栅格化），随后收回为收起态。
 *  因为面板常驻（层/tile 不销毁），这次的 tile 会存到下个首开，③ 被提前付掉。 */
function warmRaster(times = 3): void {
  if (warmed) return;
  warmed = true;
  warming.value = true;
  let n = 0;
  const tick = (): void => {
    if (++n >= times) {
      warming.value = false;
      return;
    }
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
}

/* ---------- 右滑收起手势 ---------- */
const DRAG_THRESHOLD = 96; // 拖拽超过此偏移量释放即收起（像素）
const drawerTranslateX = ref(0); // 面板当前拖动偏移量（≥0，右滑为正）
const tracking = ref(false); // 是否正在拖拽——拖拽期间用内联 transform 覆盖开路/关定位
let startX = 0;
let startOffset = 0;
let pointerId = -1;

function onHandlePointerDown(e: PointerEvent): void {
  // 只跟踪主指针的水平拖拽，避免干扰抽屉内部纵向滚动（touch-action: pan-y）。
  if (e.pointerType !== 'touch' && e.pointerType !== 'pen') return;
  tracking.value = true;
  pointerId = e.pointerId;
  startX = e.clientX;
  startOffset = drawerTranslateX.value;
  (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
}

function onHandlePointerMove(e: PointerEvent): void {
  if (!tracking.value || e.pointerId !== pointerId) return;
  const delta = e.clientX - startX;
  if (delta <= 0) {
    // 用户没有向右拖动——归零，允许垂直滚动/点击
    drawerTranslateX.value = 0;
    return;
  }
  drawerTranslateX.value = startOffset + delta;
}

function finishSwipe(e: PointerEvent): void {
  if (!tracking.value || e.pointerId !== pointerId) return;
  tracking.value = false;
  pointerId = -1;
  if (drawerTranslateX.value >= DRAG_THRESHOLD) {
    emit('close');
  } else {
    drawerTranslateX.value = 0;
  }
}

/** 点抽屉外任意处——关闭。pointerdown 早于 click，scrim 独占，不会误触面板。 */
function onScrimPointerDown(): void {
  emit('close');
}

/** Escape 关闭——仅打开态响应。组件常驻，监听器只在挂载时装一次。 */
function onDocKeydown(e: KeyboardEvent): void {
  if (e.key === 'Escape' && props.visible) emit('close');
}

onMounted(() => {
  document.addEventListener('keydown', onDocKeydown);
  // 空闲期唤醒绘制：晚于首帧、不抢主线程；缺 requestIdleCallback 时退化为定时器
  const idle = (typeof (window as any).requestIdleCallback === 'function')
    ? (cb: () => void) => (window as any).requestIdleCallback(cb, { timeout: 2000 })
    : (cb: () => void) => setTimeout(cb, 1500);
  idle(() => requestAnimationFrame(() => warmRaster()));
});
onUnmounted(() => document.removeEventListener('keydown', onDocKeydown));

/** 焦点随可见性切换：打开时把焦点移入首项，关闭时归还给打开前的触发元素。 */
watch(() => props.visible, (open) => {
  if (open) {
    previouslyFocused = document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null;
    void nextTick(() => {
      rootEl.value?.querySelector<HTMLElement>('button, [role="menuitem"]')?.focus();
    });
  } else if (previouslyFocused && previouslyFocused.isConnected) {
    previouslyFocused.focus();
  }
});
</script>

<template>
  <div
    class="drawer-host"
    :class="{ 'is-open': visible, 'is-warming': warming && !visible }"
    role="presentation"
    :inert="!visible"
  >
    <!-- 全屏遮罩（scrim）：非模态轻档 --color-scrim-menu，点它关闭。
         浮层层级与 PopupMenu 一致：面板 900 > 遮罩 899。 -->
    <div class="drawer-scrim" @pointerdown="onScrimPointerDown" />

    <aside
      ref="rootEl"
      class="drawer"
      role="menu"
      :style="tracking ? { transform: `translateX(${drawerTranslateX}px)`, transition: 'none' } : undefined"
      @pointerdown="onHandlePointerDown"
      @pointermove="onHandlePointerMove"
      @pointerup="finishSwipe"
      @pointercancel="finishSwipe"
    >
      <div class="drawer-title">
        <span class="drawer-title-text">{{ title }}</span>
      </div>

      <template v-for="(it, i) in items" :key="i">
        <div v-if="it.type === 'separator'" class="drawer-separator" role="separator" />

        <div v-else-if="it.type === 'zoom'" class="drawer-zoom" role="group" aria-label="缩放">
          <span class="drawer-zoom-label tabular-nums">缩放 {{ it.percent }}%</span>
          <div class="drawer-zoom-actions">
            <button
              type="button"
              class="drawer-zoom-btn"
              :disabled="it.canZoomOut"
              aria-label="缩小"
              @click="it.onZoomOut"
            >
              −
            </button>
            <button
              type="button"
              class="drawer-zoom-btn drawer-zoom-reset"
              aria-label="恢复 100%"
              @click="it.onZoomReset"
            >
              100%
            </button>
            <button
              type="button"
              class="drawer-zoom-btn"
              :disabled="it.canZoomIn"
              aria-label="放大"
              @click="it.onZoomIn"
            >
              +
            </button>
          </div>
        </div>

        <button
          v-else
          type="button"
          class="drawer-item"
          :class="{ danger: it.danger, active: it.active }"
          :disabled="it.disabled"
          role="menuitem"
          @click="it.onClick(); emit('close')"
        >
          <span v-if="it.icon" class="drawer-item-icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <template v-if="it.icon === 'sun'">
                <circle cx="12" cy="12" r="4" />
                <path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" />
              </template>
              <template v-else-if="it.icon === 'moon'">
                <path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z" />
              </template>
              <template v-else-if="it.icon === 'restart'">
                <path d="M3 12a9 9 0 0 1 15-6.7L21 8M21 3v5h-5M21 12a9 9 0 0 1-15 6.7L3 16M3 21v-5h5" />
              </template>
              <template v-else-if="it.icon === 'terminal'">
                <path d="M4 17l6-6-6-6M12 20h8" />
              </template>
              <template v-else-if="it.icon === 'debug'">
                <path d="M8 3H7a2 2 0 0 0-2 2v4a2 2 0 0 1-2 2 2 2 0 0 1 2 2v4a2 2 0 0 0 2 2h1M16 3h1a2 2 0 0 1 2 2v4a2 2 0 0 0 2 2 2 2 0 0 0-2 2v4a2 2 0 0 1-2 2h-1" />
              </template>
              <template v-else-if="it.icon === 'settings'">
                <circle cx="12" cy="12" r="3" />
                <path d="M19.4 15a1.7 1.7 0 0 0 .34 1.87l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.7 1.7 0 0 0-1.87-.34 1.7 1.7 0 0 0-1 1.55V21a2 2 0 1 1-4 0v-.09a1.7 1.7 0 0 0-1-1.55 1.7 1.7 0 0 0-1.87.34l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.7 1.7 0 0 0 4.6 15a1.7 1.7 0 0 0-1.55-1H3a2 2 0 1 1 0-4h.09A1.7 1.7 0 0 0 4.6 9a1.7 1.7 0 0 0-.34-1.87l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.7 1.7 0 0 0 1.87.34H9a1.7 1.7 0 0 0 1-1.55V3a2 2 0 1 1 4 0v.09a1.7 1.7 0 0 0 1 1.55 1.7 1.7 0 0 0 1.87-.34l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.7 1.7 0 0 0-.34 1.87V9a1.7 1.7 0 0 0 1.55 1H21a2 2 0 1 1 0 4h-.09a1.7 1.7 0 0 0-1.51 1z" />
              </template>
              <template v-else-if="it.icon === 'exit'">
                <path d="M18.36 6.64a9 9 0 1 1-12.73 0" />
                <path d="M12 2v10" />
              </template>
            </svg>
          </span>
          <span class="drawer-item-label">{{ it.label }}</span>
        </button>
      </template>
    </aside>
  </div>
</template>

<style scoped>
/* 常驻渲染的动效（替代原 Vue <Transition> enter/leave）+ 空闲唤醒绘制。
   .drawer-host 收起态 opacity:0 但**必须保留在渲染树**（不能 display:none / visibility:
   hidden / scale(0——那会把合成层连同 ②③ 摊销一起打掉）；inert 由模板负责收起态的
   键盘/辅助技术隐身。打开慢、收起快，与原名节点一致（enter slow / leave fast）。 */
.drawer-host {
  position: fixed;
  inset: 0;
  z-index: 899;
  pointer-events: none; /* 容器不拦截事件，实际交互由打开态的子元素接管 */
  opacity: 0;
  transition: opacity var(--motion-fast) var(--fx-ease-out); /* 关闭：快速淡出 */
}
.drawer-host.is-open {
  opacity: 1;
  transition: opacity var(--motion-slow) var(--fx-ease-in); /* 打开：慢速淡入 */
}
/* 空闲期唤醒绘制：非零透明度让真实面板真栅格化一次；面板归位（translateX(0)）、
   transition 关掉以免动画；因为常驻层/tile 不销毁，这次的 tile 存给下一个首开（消③）。 */
.drawer-host.is-warming {
  opacity: 0.01;
  transition: none;
}
.drawer-host.is-warming .drawer {
  transform: translateX(0);
  transition: none;
}
/* 唤醒期内容也归位参与栅格化（否则内容透明度 0 不绘制，tile 不完整） */
.drawer-host.is-warming .drawer-title,
.drawer-host.is-warming .drawer-item,
.drawer-host.is-warming .drawer-zoom,
.drawer-host.is-warming .drawer-separator {
  opacity: 1;
  transform: translateY(0);
  transition: none;
}
.drawer-scrim {
  position: absolute;
  inset: 0;
  background: var(--color-scrim-menu);
  pointer-events: none; /* 收起态不响应，打开态再开启 */
}
.drawer-host.is-open .drawer-scrim {
  pointer-events: auto;
}
.drawer {
  position: absolute;
  top: 0;
  bottom: 0;
  right: 0;
  z-index: 900;
  width: 320px;
  max-width: 100%;
  background: var(--color-surface-raised);
  box-shadow: var(--elevation-menu);
  border-radius: var(--radius-surface) 0 0 var(--radius-surface);
  padding: var(--space-2);
  /* 双声明：env 不可用（旧 WebView）时落到固定 padding */
  padding-bottom: var(--space-2);
  padding-bottom: calc(var(--space-2) + env(safe-area-inset-bottom));
  display: flex;
  flex-direction: column;
  overflow-y: auto;
  -webkit-overflow-scrolling: touch;
  touch-action: pan-y; /* 纵向可滚，横向拖拽留给右滑手势 */
  pointer-events: none; /* 收起态不响应，打开态再开启 */
  will-change: transform;
  transform: translateX(100%); /* 收起态面板退到屏外 */
  transition: transform var(--motion-fast) var(--fx-ease-out); /* 关闭：快速滑出 */
}
.drawer-host.is-open .drawer {
  pointer-events: auto;
  transform: translateX(0); /* 打开态面板滑入 */
  transition: transform var(--motion-slow) var(--fx-ease-in); /* 打开：慢速滑入 */
}
/* 内容逐项浮现（慢一倍，同 PopupMenu popup-item-in）：transition 只挂在 is-open 侧，
   这样关闭时无过渡即退场（被 host 快速淡出盖住），与原先只摒弃内容出场一致。 */
.drawer-title,
.drawer-item,
.drawer-zoom,
.drawer-separator {
  opacity: 0;
  transform: translateY(var(--fx-rise));
}
.drawer-host.is-open .drawer-title,
.drawer-host.is-open .drawer-item,
.drawer-host.is-open .drawer-zoom,
.drawer-host.is-open .drawer-separator {
  opacity: 1;
  transform: translateY(0);
  transition:
    opacity calc(var(--motion-slow) * 2) var(--fx-ease-in),
    transform calc(var(--motion-slow) * 2) var(--fx-ease-in);
}
/* 抽屉头部：左对齐纯文字标题，下接分隔线。非交互，仅作语义锚点。 */
.drawer-title {
  display: flex;
  align-items: center;
  min-height: var(--touch-target);
  padding: var(--space-2) var(--space-3);
  border-bottom: 1px solid var(--color-border);
  margin-bottom: var(--space-1);
}
.drawer-title-text {
  font-size: var(--font-size-lg);
  color: var(--color-text);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.drawer-item {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  min-height: var(--touch-target);
  width: 100%;
  background: transparent;
  color: var(--color-text);
  border: none;
  text-align: left;
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-control);
  font-size: var(--font-size-base);
  font-family: var(--font-ui);
  cursor: pointer;
  transition: background var(--motion-fast);
}
.drawer-item:hover:not(:disabled) {
  background: var(--color-control-hover);
}
.drawer-item.active {
  color: var(--color-text);
}
.drawer-item.danger {
  color: var(--color-text);
}
.drawer-item:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}
.drawer-item-icon {
  width: 20px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
}
.drawer-item-icon svg {
  width: 20px;
  height: 20px;
  fill: none;
  stroke: currentColor;
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
  display: block;
}
.drawer-item-label {
  flex: 1;
}
.drawer-separator {
  height: 1px;
  background: var(--color-border);
  margin: var(--space-1) var(--space-2);
}
.drawer-zoom {
  display: flex;
  flex-direction: column;
  align-items: stretch;
  gap: var(--space-2);
  padding: var(--space-2) var(--space-3);
  color: var(--color-text);
  font-size: var(--font-size-base);
}
.drawer-zoom-label {
  line-height: 24px;
}
.drawer-zoom-actions {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}
.drawer-zoom-btn {
  flex: 1;
  background: var(--color-control);
  color: var(--color-text);
  border: none;
  width: 32px;
  height: 32px;
  border-radius: var(--radius-control);
  cursor: pointer;
  font-size: var(--font-size-base);
  font-family: var(--font-ui);
  transition: background var(--motion-fast);
}
.drawer-zoom-btn:hover:not(:disabled) {
  background: var(--color-control-hover);
}
.drawer-zoom-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}
/* 「恢复 100%」按钮文字较宽，独立加宽（沿用 PopupMenu 规格） */
.drawer-zoom-reset {
  width: auto;
  min-width: 48px;
  padding: 0 var(--space-2);
  font-size: var(--font-size-sm);
}
</style>