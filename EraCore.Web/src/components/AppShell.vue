<script setup lang="ts">
withDefaults(defineProps<{ scrollable?: boolean }>(), { scrollable: false });

/**
 * AppShell.vue — 根布局壳层（ui-redesign-spec §4.1）。
 * 只负责：全局背景与 flex 布局（安全区由各壳层自行处理，避免双重叠加）、
 * 主内容 slot 与全局对话框挂载点 #dialogs。不再让根组件内联管理浮动按钮组。
 */
</script>

<template>
  <div class="app-shell" :class="{ scrollable }">
    <slot />
    <!-- 全局错误 / 确认对话框挂载点（spec §4.1） -->
    <slot name="dialogs" />
  </div>
</template>

<style scoped>
.app-shell {
  display: flex;
  flex-direction: column;
  height: 100vh;
  height: 100dvh;
  background: var(--color-bg);
  color: var(--color-text);
  font-family: var(--font-ui);
  font-size: var(--font-size-base);
  overflow: hidden;
}
.app-shell.scrollable {
  height: auto;
  /* 双声明：dvh 不可用（旧 WebView）时落到 vh */
  min-height: 100vh;
  min-height: 100dvh;
  overflow: visible;
}
</style>
