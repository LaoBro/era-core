<script setup lang="ts">
/**
 * DrawerMenu.vue — 游戏页「更多」菜单的右侧抽屉实现（替换原 PopupMenu 浮层）。
 * 触发点：游戏画面右上角 ⋮ 固定控件。顶部左对齐游戏标题，无关闭按钮；
 * 关闭方式：菜单项点击 / 点抽屉外 scrim / 右滑收起 / Escape（MAUI 物理返回键由 App.vue 统一处理）。
 * 样式沿用 ui-redesign 统一令牌：--color-surface-raised、--radius-surface、--elevation-menu；
 * 出入场为横向 slide，动效走 --motion-slow / --motion-fast。
 *
 * 由父级以 v-show（display:none）控制显示：组件随 App 常驻挂载、只在展开时才显示。
 * 因此焦点/键盘监听等副作用必须「放在展开时」而非挂载时——见下方 watch(visible)。
 * 首次打开性能由空闲期预热探针（prewarmMenuShadow.ts）覆盖，见 spec。
 */
import { ref, watch, onMounted, onUnmounted, nextTick } from 'vue';
import type { PopupMenuItem } from './menuTypes';

const props = defineProps<{
  items: PopupMenuItem[];
  /** 抽屉头部左对齐标题——游戏显示名（game.lastPlayedGame）。 */
  title: string;
  /** 展开态。组件常驻挂载，v-show 控制 display；本 prop 驱动焦点与键盘副作用。 */
  visible: boolean;
}>();

const emit = defineEmits<{
  (e: 'close'): void;
}>();

const rootEl = ref<HTMLElement | null>(null);

/** 菜单打开前获得焦点的元素——关闭时归还焦点（沿用 PopupMenu 的最小焦点管理）。 */
let previouslyFocused: HTMLElement | null = null;

/* ---------- 右滑收起手势 ---------- */
const DRAG_THRESHOLD = 96; // 拖拽超过此偏移量释放即收起（像素）
const drawerTranslateX = ref(0); // 面板当前拖动偏移量（≥0，右滑为正）
let tracking = false;
let startX = 0;
let startOffset = 0;
let pointerId = -1;

function onHandlePointerDown(e: PointerEvent): void {
  // 只跟踪主指针的水平拖拽，避免干扰抽屉内部纵向滚动（touch-action: pan-y）。
  if (e.pointerType !== 'touch' && e.pointerType !== 'pen') return;
  tracking = true;
  pointerId = e.pointerId;
  startX = e.clientX;
  startOffset = drawerTranslateX.value;
  (e.currentTarget as HTMLElement).setPointerCapture?.(e.pointerId);
}

function onHandlePointerMove(e: PointerEvent): void {
  if (!tracking || e.pointerId !== pointerId) return;
  const delta = e.clientX - startX;
  if (delta <= 0) {
    // 用户没有向右拖动——归零，允许垂直滚动/点击
    drawerTranslateX.value = 0;
    return;
  }
  drawerTranslateX.value = startOffset + delta;
}

function finishSwipe(e: PointerEvent): void {
  if (!tracking || e.pointerId !== pointerId) return;
  tracking = false;
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

/** Escape 关闭——仅展开态响应。组件常驻，监听器只在挂载时装一次。 */
function onDocKeydown(e: KeyboardEvent): void {
  if (e.key === 'Escape' && props.visible) emit('close');
}

onMounted(() => document.addEventListener('keydown', onDocKeydown));
onUnmounted(() => document.removeEventListener('keydown', onDocKeydown));

/** 焦点随展开切换：展开时把焦点移入首项，关闭时归还给展开前的触发元素。 */
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
  <div class="drawer-host" role="presentation">
    <!-- 全屏遮罩（scrim）：非模态轻档 --color-scrim-menu，点它关闭。
         浮层层级与 PopupMenu 一致：面板 900 > 遮罩 899。 -->
    <div class="drawer-scrim" @pointerdown="onScrimPointerDown" />

    <aside
      ref="rootEl"
      class="drawer"
      role="menu"
      :style="{
        transform: `translateX(${drawerTranslateX}px)`,
        transition: tracking ? 'none' : 'transform var(--motion-slow) var(--fx-ease-in)',
      }"
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
.drawer-host {
  position: fixed;
  inset: 0;
  z-index: 899;
  pointer-events: none; /* 容器不拦截事件，实际交互由子元素接管 */
}
.drawer-scrim {
  position: absolute;
  inset: 0;
  background: var(--color-scrim-menu);
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
  pointer-events: auto;
  will-change: transform;
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