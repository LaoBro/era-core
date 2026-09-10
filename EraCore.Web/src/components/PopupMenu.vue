<script lang="ts">
/**
 * PopupMenu.vue — 游戏选择页的 Android 风格 Popup Menu（ui-redesign-spec §6.1），
 * 仍用于 MauiGameList / WebGameList 的「目录 / 更多」浮层菜单；游戏页「更多」已改用 DrawerMenu。
 * 高不透明度 --color-surface-raised、单档 --elevation-menu、--radius-surface(12px)；
 * 菜单项最小高度 48px，左侧图标、右侧文字；缩放作为菜单项（当前百分比 + 缩/放）；
 * 退出项与其它项之间用分隔线；点击外部或按 Escape 关闭（spec §6.1）。
 * 普通菜单项点击后自动关闭；出入场缩放动画由父级 <Transition name="popup"> 驱动。
 */
import type { IconName, PopupMenuItem } from './menuTypes';
export type { IconName, PopupMenuItem };
</script>

<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue';

defineProps<{ items: PopupMenuItem[] }>();

const emit = defineEmits<{
  (e: 'close'): void;
}>();

const rootEl = ref<HTMLElement | null>(null);

/** 菜单打开前获得焦点的元素——关闭时归还焦点（方案 3 最小焦点管理）。 */
let previouslyFocused: HTMLElement | null = null;

/** 点击菜单外部——关闭。mousedown 早于 click，避免菜单内点击冒泡误判。 */
function onDocPointerDown(e: MouseEvent): void {
  if (rootEl.value && !rootEl.value.contains(e.target as Node)) {
    emit('close');
  }
}

/** Escape——关闭。 */
function onDocKeydown(e: KeyboardEvent): void {
  if (e.key === 'Escape') emit('close');
}

onMounted(() => {
  document.addEventListener('mousedown', onDocPointerDown);
  document.addEventListener('keydown', onDocKeydown);
  // 焦点管理（spec §9）：记录打开前的焦点，并把焦点移入第一个可交互项
  previouslyFocused = document.activeElement instanceof HTMLElement
    ? document.activeElement
    : null;
  const firstInteractive = rootEl.value?.querySelector<HTMLElement>(
    'button, [role="menuitem"]',
  );
  firstInteractive?.focus();
});
onUnmounted(() => {
  document.removeEventListener('mousedown', onDocPointerDown);
  document.removeEventListener('keydown', onDocKeydown);
  // 方案 3：关闭时把焦点归还给打开菜单的触发元素
  if (previouslyFocused && previouslyFocused.isConnected) {
    previouslyFocused.focus();
  }
});
</script>

<template>
  <div class="popup-root" role="presentation">
    <div ref="rootEl" class="popup-menu" role="menu">
    <template v-for="(it, i) in items" :key="i">
      <div v-if="it.type === 'separator'" class="popup-separator" role="separator" />

      <div v-else-if="it.type === 'zoom'" class="popup-zoom" role="group" aria-label="缩放">
        <span class="popup-zoom-label tabular-nums">缩放 {{ it.percent }}%</span>
        <div class="popup-zoom-actions">
          <button
            type="button"
            class="popup-zoom-btn"
            :disabled="it.canZoomOut"
            aria-label="缩小"
            @click="it.onZoomOut"
          >
            −
          </button>
          <button
            type="button"
            class="popup-zoom-btn popup-zoom-reset"
            aria-label="恢复 100%"
            @click="it.onZoomReset"
          >
            100%
          </button>
          <button
            type="button"
            class="popup-zoom-btn"
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
        class="popup-item"
        :class="{ danger: it.danger, active: it.active }"
        :disabled="it.disabled"
        role="menuitem"
        @click="it.onClick(); emit('close')"
      >
        <span v-if="it.icon" class="popup-item-icon" aria-hidden="true">
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
        <span class="popup-item-label">{{ it.label }}</span>
        <!-- issues/15 S3：右侧弱化副文案（可选）——把「这条路的代价」写在点之前 -->
        <span v-if="it.hint" class="popup-item-hint">{{ it.hint }}</span>
      </button>
    </template>
    </div>
  </div>
</template>

<style scoped>
/* 全屏遮罩（scrim）：作为 Transition 根元素，淡入淡出；点击遮罩区域走
   onDocPointerDown 的「rootEl 之外」分支关闭菜单。
   非模态浮层用轻档 --color-scrim-menu，强模态 ConfirmDialog 用 --color-overlay
   （浮层层级：dialog ≥1000 > menu 面板 900 > menu 遮罩 899） */
.popup-root {
  position: fixed;
  inset: 0;
  z-index: 899;
  background: var(--color-scrim-menu);
}
.popup-menu {
  position: fixed;
  top: calc(var(--appbar-height) + var(--space-2));
  right: var(--space-3);
  z-index: 900;
  min-width: 200px;
  background: var(--color-surface-raised);
  border: none;
  border-radius: var(--radius-surface);
  box-shadow: var(--elevation-menu);
  padding: var(--space-1);
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.popup-item {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  min-height: var(--touch-target);
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
.popup-item:hover:not(:disabled) {
  background: var(--color-control-hover);
}
.popup-item.active {
  color: var(--color-text);
}
.popup-item.danger {
  color: var(--color-text);
}
.popup-item:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}
.popup-item-icon {
  width: 20px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
}
.popup-item-icon svg {
  width: 20px;
  height: 20px;
  fill: none;
  stroke: currentColor;
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
  display: block;
}
.popup-item-label {
  flex: 1;
}
/* issues/15 S3：菜单项右侧副文案——弱化、不抢主标签，且不换行 */
.popup-item-hint {
  flex-shrink: 0;
  margin-left: var(--space-3);
  color: var(--color-text-muted);
  font-size: var(--font-size-sm);
  opacity: 0.75;
  white-space: nowrap;
}
.popup-separator {
  height: 1px;
  background: var(--color-border);
  margin: var(--space-1) var(--space-2);
}
.popup-zoom {
  display: flex;
  flex-direction: column;
  align-items: stretch;
  gap: var(--space-2);
  padding: var(--space-2) var(--space-3);
  color: var(--color-text);
  font-size: var(--font-size-base);
}
.popup-zoom-label {
  line-height: 24px;
}
.popup-zoom-actions {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}
.popup-zoom-btn {
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
.popup-zoom-btn:hover:not(:disabled) {
  background: var(--color-control-hover);
}
.popup-zoom-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}
/* 「恢复 100%」按钮文字较宽，独立加宽 */
.popup-zoom-reset {
  width: auto;
  min-width: 48px;
  padding: 0 var(--space-2);
  font-size: var(--font-size-sm);
}
</style>
