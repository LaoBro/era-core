<script setup lang="ts">
/**
 * StatusBanner.vue — 顶部可关闭提示条（ui-redesign-spec §4 组件清单）。
 * 用于连接 / 错误 / 重连 / 加载提示；错误不只靠颜色表达（spec §9）。
 */
withDefaults(
  defineProps<{
    /** 语义类别——决定配色（错误 / 警告 / 成功 / 信息）。 */
    kind?: 'error' | 'warning' | 'success' | 'info';
    /** 是否显示关闭按钮。 */
    closable?: boolean;
    /** 文本是否可换行（长错误）。 */
    wrap?: boolean;
  }>(),
  { kind: 'info', closable: false, wrap: true },
);

const emit = defineEmits<{
  (e: 'close'): void;
}>();
</script>

<template>
  <div class="status-banner" :class="kind" role="status">
    <span class="status-banner-text" :class="{ wrap }">
      <slot />
    </span>
    <button
      v-if="closable"
      type="button"
      class="status-banner-close"
      aria-label="关闭提示"
      @click="emit('close')"
    >
      ×
    </button>
  </div>
</template>

<style scoped>
.status-banner {
  display: flex;
  align-items: flex-start;
  gap: var(--space-2);
  padding: var(--space-2) var(--space-3);
  border-radius: var(--radius-control);
  font-size: var(--font-size-sm);
  line-height: 1.5;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  color: var(--color-text-muted);
  transition: background var(--motion-fast), border-color var(--motion-fast);
}
.status-banner.error {
  /* 双声明：旧 WebView 无 color-mix → 落到纯底色 + 满强度边框，保留可辨识的状态色 */
  background: var(--color-bg);
  background: color-mix(in srgb, var(--color-error) 14%, var(--color-surface));
  border-color: var(--color-error);
  border-color: color-mix(in srgb, var(--color-error) 45%, var(--color-border));
  color: var(--color-error);
}
.status-banner.warning {
  background: var(--color-bg);
  background: color-mix(in srgb, var(--color-warning) 14%, var(--color-surface));
  border-color: var(--color-warning);
  border-color: color-mix(in srgb, var(--color-warning) 45%, var(--color-border));
  color: var(--color-warning);
}
.status-banner.success {
  background: var(--color-bg);
  background: color-mix(in srgb, var(--color-success) 14%, var(--color-surface));
  border-color: var(--color-success);
  border-color: color-mix(in srgb, var(--color-success) 45%, var(--color-border));
  color: var(--color-success);
}
.status-banner-text {
  flex: 1;
  min-width: 0;
}
.status-banner-text:not(.wrap) {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.status-banner-close {
  background: transparent;
  border: none;
  color: inherit;
  cursor: pointer;
  font-size: 16px;
  line-height: 1;
  padding: var(--space-1);
  border-radius: var(--radius-control);
  flex-shrink: 0;
}
.status-banner-close:hover {
  background: var(--color-surface-raised);
}
</style>
