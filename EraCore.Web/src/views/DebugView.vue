<script setup lang="ts">
import { ref } from 'vue';
import { useConnectionStore } from '../stores/connection';
import { useGameStore } from '../stores/game';
import { isMauiEnvironment } from '../lib/mauiBridge';

const conn = useConnectionStore();
const game = useGameStore();
const isMaui = isMauiEnvironment();
const inputValue = ref<string>('');

function onSend(): void {
  const v = inputValue.value;
  if (!v || !conn.canInput) return;
  void conn.sendInput(v);
  // 不清空输入框，方便反复发送同一输入调试
}
</script>

<template>
  <section class="debug-view">
    <div class="left-pane">
      <!-- MAUI 模式无 HTTP/WS，无 snapshot 概念——隐藏 snapshot 区，仅保留最新帧区。
           HTTP 模式 snapshot 由 WS onopen 后 GET /snapshot 拉取，晚加入者画面恢复用。 -->
      <div v-if="!isMaui" class="snapshot-section">
        <h3>最近 Snapshot（GET /snapshot）</h3>
        <pre v-if="game.lastSnapshot" class="raw-json snapshot-json">{{
          JSON.stringify(game.lastSnapshot, null, 2)
        }}</pre>
        <div v-else class="empty">尚未拉取 snapshot（连接建立后由 onopen 触发）</div>
      </div>
      <div class="latest-frame-section">
        <h3>最新 WS 帧（原始 JSON）</h3>
        <pre v-if="game.lastTurnJson" class="raw-json">{{ game.lastTurnJson }}</pre>
        <div v-else class="empty">等待 WS 帧…</div>
        <p v-if="game.lastError" class="error">解析错误：{{ game.lastError }}</p>
      </div>
    </div>
    <div class="right-pane">
      <div class="input-section">
        <h3>提交输入</h3>
        <div class="input-row">
          <input
            v-model="inputValue"
            class="text-input"
            type="text"
            placeholder="输入值（如 0）"
            :disabled="conn.status !== 'connected' || !conn.canInput"
            @keyup.enter="onSend"
          />
          <button
            class="btn-primary"
            :disabled="conn.status !== 'connected' || !conn.canInput"
            @click="onSend"
          >
            发送
          </button>
        </div>
        <p class="hint">帧格式：<code>{"type":"input","value":"..."}</code></p>
      </div>
      <div class="history-section">
        <h3>历史帧（{{ game.turnHistory.length }}）</h3>
        <div class="history-list">
          <div
            v-for="(frame, idx) in game.turnHistory.slice().reverse()"
            :key="idx"
            class="history-item"
          >
            <span class="frame-idx">#{{ game.turnHistory.length - idx }}</span>
            <pre class="frame-json">{{ frame }}</pre>
          </div>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.debug-view {
  display: flex;
  width: 100%;
  height: 100%;
  gap: 1px;
  background: var(--color-border);
}
.left-pane,
.right-pane {
  flex: 1;
  background: var(--color-bg);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.snapshot-section,
.latest-frame-section {
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.snapshot-section {
  flex: 0 0 45%;
  border-bottom: 2px solid var(--color-border);
}
.latest-frame-section {
  flex: 1;
  min-height: 0;
}
h3 {
  margin: 0;
  padding: var(--space-2) var(--space-3);
  background: var(--color-surface);
  font-size: var(--font-size-md);
  color: var(--color-indicator);
  border-bottom: 1px solid var(--color-border);
  flex-shrink: 0;
}
.snapshot-section h3 {
  color: var(--color-warning);
}
.raw-json,
.frame-json {
  margin: 0;
  padding: var(--space-3);
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
  white-space: pre-wrap;
  word-break: break-all;
  overflow: auto;
  color: var(--color-text);
}
.raw-json {
  flex: 1;
  overflow: auto;
}
.snapshot-json {
  color: var(--color-warning);
  background: var(--color-bg);
}
.empty {
  padding: var(--space-4);
  color: var(--color-text-muted);
  font-style: italic;
}
.error {
  margin: 0;
  padding: var(--space-2) var(--space-3);
  color: var(--color-error);
  /* 双声明：旧 WebView 无 color-mix → 落到纯底色 */
  background: var(--color-bg);
  background: color-mix(in srgb, var(--color-error) 14%, var(--color-surface));
  border-top: none;
  font-size: var(--font-size-sm);
  flex-shrink: 0;
}
.right-pane {
  display: flex;
  flex-direction: column;
}
.input-section {
  padding: 0;
  border-bottom: 1px solid var(--color-border);
  flex-shrink: 0;
}
.input-row {
  display: flex;
  gap: var(--space-1);
  padding: var(--space-2) var(--space-3);
}
.text-input {
  flex: 1;
  background: var(--color-control);
  color: var(--color-text);
  border: none;
  padding: 6px 14px;
  border-radius: var(--radius-control);
  font-family: var(--font-ui);
  font-size: var(--font-size-base);
  min-height: 40px;
  transition: background var(--motion-fast);
}
/* 聚焦仅微调背景；键盘焦点由全局 :focus-visible 焦点环提供 */
.text-input:focus {
  background: var(--color-control-hover);
}
.text-input:disabled {
  opacity: 0.5;
}
.hint {
  margin: 0;
  padding: var(--space-1) var(--space-3) var(--space-2);
  color: var(--color-text-muted);
  font-size: var(--font-size-xs);
}
.hint code {
  color: var(--color-warning);
  background: var(--color-surface);
  padding: 1px 4px;
  border-radius: 2px;
}
.history-section {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}
.history-list {
  flex: 1;
  overflow: auto;
}
.history-item {
  display: flex;
  flex-direction: column;
  border-bottom: 1px solid var(--color-border);
}
.frame-idx {
  padding: 2px var(--space-3);
  font-size: var(--font-size-xs);
  color: var(--color-indicator);
  font-family: var(--font-mono);
  background: var(--color-surface);
}

.frame-json {
  font-size: var(--font-size-xs);
  max-height: 120px;
  overflow: auto;
  padding: var(--space-1) var(--space-3);
  color: var(--color-text-muted);
}
</style>
