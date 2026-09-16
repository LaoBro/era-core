<script setup lang="ts">
import { useConnectionStore } from '../stores/connection';

/**
 * 旁观横幅：agent 持有控制权时禁用输入，并提供接管入口。
 */
const conn = useConnectionStore();

function onTakeOver(): void {
  void conn.acquireControl();
}
</script>

<template>
  <div v-if="conn.isSpectator" class="spectator-banner" role="status">
    <div class="spectator-copy">
      <strong>旁观中</strong>
      <span>Agent 正在操控，接管后可操作</span>
    </div>
    <button type="button" class="btn-primary take-over" @click="onTakeOver">
      接管
    </button>
  </div>
</template>

<style scoped>
.spectator-banner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-control);
  /* 双声明：旧 WebView 无 color-mix → 落到纯底色 + 满强度边框
     （shorthand border 整条退场，这里补完整声明而非仅 border-color） */
  background: var(--color-bg);
  background: color-mix(in srgb, var(--color-warning) 14%, var(--color-surface));
  border: 1px solid var(--color-warning);
  border: 1px solid color-mix(in srgb, var(--color-warning) 45%, var(--color-border));
  color: var(--color-warning);
}
.spectator-copy {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
  font-size: var(--font-size-sm);
}
.spectator-copy strong {
  color: var(--color-text);
  font-size: var(--font-size-md);
}
.take-over {
  flex-shrink: 0;
}
</style>
