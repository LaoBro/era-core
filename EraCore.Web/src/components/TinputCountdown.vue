<script setup lang="ts">
import { computed } from 'vue';
import { useGameStore } from '../stores/game';

/**
 * TinputCountdown.vue — TINPUT 实时倒计时轻量组件（ADR-0016）。
 *
 * 仅依赖 game store 的 tinputRemainingMs / tinputTimeLimit / showTinputCountdown，
 * 不接触 displayState，避免因 displayState 变化触发不必要的重渲染。
 */
const game = useGameStore();

/** 倒计时进度条 max 值——总毫秒数。null 时不渲染。 */
const tinputProgressMax = computed<number | null>(() => game.tinputTimeLimit);

/** 倒计时进度条 value 值——剩余毫秒数。null 时不渲染。 */
const tinputProgressValue = computed<number | null>(() => game.tinputRemainingMs);

/** 倒计时显示文案——剩余秒数（保留 1 位小数）。null 时不显示。 */
const tinputCountdownText = computed<string | null>(() => {
  const remaining = game.tinputRemainingMs;
  if (remaining === null) return null;
  const seconds = remaining / 1000;
  return `${seconds.toFixed(1)}s`;
});
</script>

<template>
  <div v-if="game.showTinputCountdown" class="tinput-countdown">
    <progress
      class="tinput-progress"
      :max="tinputProgressMax ?? 1"
      :value="tinputProgressValue ?? 0"
    ></progress>
    <span class="tinput-countdown-text">⏱ {{ tinputCountdownText ?? '' }}</span>
  </div>
</template>

<style scoped>
.tinput-countdown {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-1) var(--space-2);
  /* 双声明：旧 WebView 无 color-mix → 落到纯底色 */
  background: var(--color-bg);
  background: color-mix(in srgb, var(--color-warning) 12%, var(--color-surface));
  border-radius: var(--radius-control);
  border: none;
  font-size: var(--font-size-sm);
  color: var(--color-warning);
}
.tinput-progress {
  flex: 1;
  height: 6px;
  background: var(--color-bg);
  border: none;
  border-radius: 2px;
  /* progress 元素原生外观重置 */
  -webkit-appearance: none;
  appearance: none;
}
.tinput-progress::-webkit-progress-bar {
  background: var(--color-bg);
  border-radius: 2px;
}
.tinput-progress::-webkit-progress-value {
  background: var(--color-indicator);
  border-radius: 2px;
  transition: width var(--motion-fast) linear;
}
.tinput-progress::-moz-progress-bar {
  background: var(--color-indicator);
  border-radius: 2px;
}
.tinput-countdown-text {
  font-family: var(--font-mono);
  min-width: 60px;
  text-align: right;
}
</style>
