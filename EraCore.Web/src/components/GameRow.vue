<script setup lang="ts">
import { computed, ref } from 'vue';
import PopupMenu from './PopupMenu.vue';
import type { PopupMenuItem } from './menuTypes';
/**
 * GameRow.vue — 游戏选择页列表项（ui-redesign-spec §4 组件清单 / §5.3 + DESIGN.md）。
 * 安卓文件管理器式整行布局：左侧文件夹图标、中间游戏名、右侧进入箭头。
 * 上次游玩项使用 --color-surface-raised 与 left-indicator-bar（4px 天蓝，DESIGN.md），
 * 不使用强烈彩色背景。
 *
 * issues/05：IR 缓存存在性提示徽章——存在性语义（非有效性承诺），行内显示大小、
 * 构建时间放 tooltip；游戏更新后下次加载自动重建，提示随下次库刷新更新。
 */
const props = defineProps<{
  /** 游戏名。 */
  name: string;
  /** 是否上次游玩——列表高亮（left-indicator-bar）。 */
  lastPlayed?: boolean;
  /** 是否存在 IR 缓存（era_ir.dat 存在且非空）。 */
  irCached?: boolean;
  /** IR 缓存字节数（era_ir.dat 大小）。 */
  irCacheBytes?: number;
  /** IR 缓存构建时间（本地时区 "yyyy-MM-dd HH:mm"）。 */
  irCacheBuiltUtc?: string | null;
}>();

const emit = defineEmits<{
  (e: 'click'): void;
  (e: 'rescan'): void;
}>();

/** 相对时间（徽章内联展示，响应式跟随 prop）。 */
const relTime = computed(() => formatRelTime(props.irCacheBuiltUtc));

/** ⋮ 菜单项——路线 2 显式失效的唯一入口（issues/13 裁定：仅库卡片菜单）。 */
const menuItems: PopupMenuItem[] = [
  {
    type: 'item',
    id: 'rescan-ir-cache',
    label: '重扫脚本缓存',
    danger: true,
    onClick: onRescanItemClick,
  },
];

/** 字节数 → 人类可读 MB 文案（保留一位小数，≥100MB 取整；无数据返回空）。 */
function formatIrMb(bytes?: number): string {
  const b = typeof bytes === 'number' && bytes > 0 ? bytes : 0;
  if (b <= 0) return '';
  const mb = b / (1024 * 1024);
  return mb >= 100 ? `${Math.round(mb)}MB` : `${mb.toFixed(1).replace(/\.0$/, '')}MB`;
}

/**
 * issues/13 M5：建库时间的相对形式——徽章内联展示（安卓触屏无 hover，tooltip 不可见，
 * 「陈旧可见」要求时间一眼可读）。"yyyy-MM-dd HH:mm" 本地时区串手工解析（避免 WebView
 * 对非 ISO 串的解析差异）；≥60 天回退绝对日期。
 */
function formatRelTime(builtUtc?: string | null): string {
  if (!builtUtc) return '';
  const m = /^(\d{4})-(\d{2})-(\d{2}) (\d{2}):(\d{2})$/.exec(builtUtc);
  if (!m) return '';
  const built = new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3]), Number(m[4]), Number(m[5]));
  const diffMs = Date.now() - built.getTime();
  if (Number.isNaN(diffMs) || diffMs < 0) return '';
  const min = Math.floor(diffMs / 60000);
  if (min < 1) return '刚刚';
  if (min < 60) return `${min}分钟前`;
  const hr = Math.floor(min / 60);
  if (hr < 24) return `${hr}小时前`;
  const day = Math.floor(hr / 24);
  if (day < 60) return `${day}天前`;
  return `${m[1]}-${m[2]}-${m[3]}`;
}

// issues/13 M5：卡片 ⋮ 菜单（仅 irCached 时渲染）——「重扫脚本缓存」入口（路线 2 显式失效）
const menuOpen = ref(false);
function onRescanItemClick(): void {
  menuOpen.value = false;
  emit('rescan');
}
</script>

<template>
  <li class="game-row" :class="{ 'last-played': lastPlayed }">
    <button type="button" class="game-row-btn" @click="$emit('click')">
      <span class="game-row-icon" aria-hidden="true">
        <svg viewBox="0 0 24 24" role="img">
          <path d="M3 6.5A2.5 2.5 0 0 1 5.5 4H10l2 2h6.5A2.5 2.5 0 0 1 21 8.5v9A2.5 2.5 0 0 1 18.5 20h-13A2.5 2.5 0 0 1 3 17.5z" />
        </svg>
      </span>
      <span class="game-row-main">
        <span class="game-row-name">{{ name }}</span>
        <span v-if="lastPlayed" class="game-row-meta">上次游玩</span>
        <span
          v-if="irCached"
          class="game-row-meta game-row-ir"
          :title="irCacheBuiltUtc ? `IR 缓存构建于 ${irCacheBuiltUtc}` : 'IR 缓存'"
        >IR {{ formatIrMb(irCacheBytes) }}<template v-if="relTime"> · {{ relTime }}</template></span>
      </span>
      <span class="game-row-arrow" aria-hidden="true">
        <svg viewBox="0 0 24 24" role="img">
          <path d="m9 18 6-6-6-6" />
        </svg>
      </span>
    </button>
    <button
      v-if="irCached"
      type="button"
      class="game-row-menu-btn"
      aria-label="更多操作"
      @click.stop="menuOpen = true"
    >
      <svg viewBox="0 0 24 24" role="img" aria-hidden="true">
        <circle cx="12" cy="5" r="1.6" /><circle cx="12" cy="12" r="1.6" /><circle cx="12" cy="19" r="1.6" />
      </svg>
    </button>
    <Transition name="popup">
      <PopupMenu
        v-if="menuOpen"
        class="game-row-menu"
        :items="menuItems"
        @close="menuOpen = false"
      />
    </Transition>
  </li>
</template>

<style scoped>
.game-row {
  position: relative;
  list-style: none;
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  align-items: center;
  border-radius: var(--radius-control);
  transition: background-color var(--motion-mid) ease;
}
/* issues/13 M5：卡片 ⋮ 按钮（仅 irCached 渲染）——「重扫脚本缓存」入口 */
.game-row-menu-btn {
  width: 40px;
  height: 48px;
  margin-right: 4px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: transparent;
  color: var(--color-text);
  border: none;
  border-radius: 9999px;
  cursor: pointer;
  transition: background-color var(--motion-fast);
}
.game-row-menu-btn:hover {
  background: var(--state-layer-hover);
}
.game-row-menu-btn:active {
  background: var(--state-layer-active);
}
.game-row-menu-btn:focus-visible {
  background: var(--state-layer-focus);
  outline: 2px solid var(--color-focus);
  outline-offset: -2px;
}
.game-row-menu-btn svg {
  width: 22px;
  height: 22px;
  fill: currentColor;
  stroke: none;
}
.game-row-menu {
  position: absolute;
  right: 8px;
  top: calc(100% - 6px);
  z-index: 30;
  min-width: 176px;
}
.game-row.last-played {
  background: transparent;
}
.game-row.last-played::before {
  content: "";
  position: absolute;
  left: 0;
  top: 10px;
  bottom: 10px;
  width: 4px;
  background: var(--color-indicator);
  border-radius: var(--radius-indicator);
  z-index: 2;
}
.game-row-btn {
  display: grid;
  grid-template-columns: 48px minmax(0, 1fr) 48px;
  align-items: center;
  width: 100%;
  min-height: var(--row-min-height);
  text-align: left;
  background: transparent;
  color: var(--color-text);
  border: none;
  padding: 0;
  border-radius: var(--radius-control);
  cursor: pointer;
  font-size: var(--font-size-base);
  font-family: var(--font-ui);
  transition: background-color var(--motion-fast);
}
.game-row-btn:hover {
  background: var(--state-layer-hover);
}
.game-row-btn:focus-visible {
  background: var(--state-layer-focus);
  outline: 2px solid var(--color-focus);
  outline-offset: -2px;
}
.game-row-btn:active {
  background: var(--state-layer-active);
}
.game-row-icon {
  color: var(--color-text-muted);
  justify-self: center;
  display: flex;
  align-items: center;
}
.game-row-icon svg,
.game-row-arrow svg {
  width: 24px;
  height: 24px;
  fill: none;
  stroke: currentColor;
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
}
.game-row-main {
  display: flex;
  align-items: center;
  justify-content: flex-start;
  gap: 6px;
  min-width: 0;
  height: 100%;
}
.game-row-name {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  line-height: 24px;
  color: var(--color-text);
}
.game-row-meta {
  flex-shrink: 0;
  margin-left: 8px;
  color: var(--color-text-muted);
  opacity: 0.65;
  font-size: var(--font-size-sm);
  white-space: nowrap;
}
.game-row-meta::before {
  content: "|";
  margin-right: 8px;
  opacity: 0.4;
}
/* issues/05：IR 缓存存在性提示徽章——成功色弱化，提示仅辅助（不喧宾夺主） */
.game-row-ir {
  color: var(--color-success);
  opacity: 0.85;
}
.game-row-arrow {
  color: var(--color-text-muted);
  justify-self: center;
  display: flex;
  align-items: center;
  opacity: 0.7;
}
</style>
