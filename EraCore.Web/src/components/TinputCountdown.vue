<script setup lang="ts">
import { computed } from 'vue';
import { useGameStore } from '../stores/game';

/**
 * TinputCountdown.vue — 定时等待覆盖层：倒计时（ADR-0016）。
 *
 * 只读 game store 的派生量（tinputRemainingMs / tinputTimeLimit / showTinputCountdown），
 * 不碰 displayState 大对象——避免因 displayState 变化触发不必要的重渲染。
 *
 * 🔴 issue 10 撤回：这里曾有过第二种呈现「跳过」按钮（`canSkipWait`，依据
 * `awaiting.wait.interruptible`）。它是 era-core 自创语义——参考实现里 `AWAIT` 是 Sleep 态、
 * 不建立等待，输入无从打断（spec §10.1）。实测在 eraTW `@BADAPPLE` 上，脚本在「落后/领先」
 * 两个分支间逐帧抖动，按钮随之反复挂载/卸载 ⇒ 顶部持续闪烁。字段与 UI 一并撤回后，
 * 定时等待不再有任何 affordance：等待只由服务端定时器结束。
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
