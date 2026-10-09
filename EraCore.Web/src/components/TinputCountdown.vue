<script setup lang="ts">
import { computed } from 'vue';
import { useGameStore } from '../stores/game';
import { useConnectionStore } from '../stores/connection';

/**
 * TinputCountdown.vue — 定时等待覆盖层：倒计时（ADR-0016）+ 「跳过」提示（issue 07）。
 *
 * 只读 game store 的派生量（tinputRemainingMs / tinputTimeLimit / showTinputCountdown /
 * canSkipWait），不碰 displayState 大对象——避免因 displayState 变化触发不必要的重渲染。
 *
 * 两种呈现互不排斥：
 * - **倒计时**：`showTinputCountdown`（带上界的等待 + ERB 允许显示）——进度条 + 剩余秒数；
 * - **跳过**：`canSkipWait`（可打断的定时等待，`awaiting.wait.interruptible`）——点击提交
 *   空输入提前结束等待（服务端 `Skippable=true` 时接受输入打断，见 spec §2.4）。
 *
 * 🔒 零时长让路帧两者都不给：`canSkipWait` 要求 `durationMs > 0`，而让路的 wait 既无
 * `interruptible` 键、`durationMs` 也是 0——它表达的是「下一帧立刻到」，不是需要玩家等待的东西。
 */
const game = useGameStore();
const conn = useConnectionStore();

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

/** 跳过可点：连接可用 + 非旁观（canInput）+ 无在途输入。 */
const canSkip = computed<boolean>(
  () => conn.status === 'connected' && conn.canInput && !game.inputInFlight,
);

/**
 * 跳过当前等待——提交空输入。与 AnyKey 的「推进」同路径（服务端在 `Skippable=true`
 * 的等待上接受任意输入并提前结束它）；乐观锁防连点。
 */
function skipWait(): void {
  if (!canSkip.value) return;
  game.setInputInFlight();
  void conn.sendInput('');
}
</script>

<template>
  <div v-if="game.showTinputCountdown || game.canSkipWait" class="tinput-countdown">
    <template v-if="game.showTinputCountdown">
      <progress
        class="tinput-progress"
        :max="tinputProgressMax ?? 1"
        :value="tinputProgressValue ?? 0"
      ></progress>
      <span class="tinput-countdown-text">⏱ {{ tinputCountdownText ?? '' }}</span>
    </template>
    <button
      v-if="game.canSkipWait"
      class="tinput-skip"
      type="button"
      :disabled="!canSkip"
      @click="skipWait"
    >
      跳过
    </button>
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
/* 「跳过」控件——借用全局控件底色 + 强调色文字，与输入栏控件同圆角规格 */
.tinput-skip {
  min-height: 32px;
  padding: 0 var(--space-2);
  background: var(--color-control);
  color: var(--color-indicator);
  border: none;
  border-radius: var(--radius-control);
  font-family: var(--font-ui);
  font-size: var(--font-size-sm);
  cursor: pointer;
}
.tinput-skip:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
</style>
