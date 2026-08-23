<script setup lang="ts">
import { ref } from 'vue';
import { useGameStore } from '../stores/game';
import { useUiStore } from '../stores/ui';
import { setAgentLogEnabled, setDisplayReportOverride, getAgentLog, exportAgentLog } from '../lib/mauiBridge';
import type { DisplayReportOverride } from '../lib/mauiBridge';

const game = useGameStore();
const ui = useUiStore();

/**
 * A0（saf-accel 计划）：文件日志开关切换——投递 setAgentLogEnabled 请求 C# 切换 +
 * 持久化，本地乐观更新。C# 处理完后推回 config 消息（含 agentLogEnabled 字段）
 * 再次同步——最终显示以 C# 权威状态为准。
 */
function toggleAgentLog(): void {
  const next = !game.agentLogEnabled;
  setAgentLogEnabled(next);
  game.agentLogEnabled = next;
}

/**
 * 启动日志覆盖三态切换——投递 setDisplayReportOverride 请求 C# 覆盖游戏
 * `DisplayReport` + 持久化，本地乐观更新。C# 处理完推回 config 消息
 * （含 displayReportOverride 字段）再次同步——最终显示以 C# 权威状态为准。
 */
function chooseDisplayReportOverride(value: DisplayReportOverride): void {
  setDisplayReportOverride(value);
  game.displayReportOverride = value;
}

/**
 * A0 补充（真机无 adb）：请求 C# 读取 agent.log 内容——回复异步到达（agentLog 消息
 * → useAppInit 写入 store），展示区由 store 驱动自动渲染。
 */
function viewLog(): void {
  logCopied.value = false;
  getAgentLog();
}

/**
 * A0 补充（真机无 adb）：导出 agent.log 文件——C# 弹系统分享面板（FileProvider），
 * 绕开 WebView 剪贴板复制 200K 文字的限制。文件在 app 私有目录，经分享可保存/转发。
 */
function exportLog(): void {
  exportAgentLog();
}

/** 复制日志全文到剪贴板——navigator.clipboard 不可用时提示手动长按选择。 */
async function copyLog(): Promise<void> {
  try {
    await navigator.clipboard.writeText(game.agentLogContent);
    logCopied.value = true;
    setTimeout(() => (logCopied.value = false), 1500);
  } catch {
    logCopied.value = false;
  }
}

const logCopied = ref(false);
</script>

<template>
  <section class="settings-view">
    <div class="settings-content">
      <div class="section">
        <h3>基本配置（emuera.config）</h3>
        <div class="setting-row">
          <span class="setting-label">历史日志行数</span>
          <span class="setting-value">{{ game.maxLog ?? '—' }}</span>
        </div>
      </div>
      <div class="section">
        <h3>外观</h3>
        <div class="setting-row">
          <span class="setting-label">亮色主题</span>
          <button
            class="toggle"
            :class="{ on: ui.theme === 'light' }"
            role="switch"
            :aria-checked="ui.theme === 'light'"
            @click="ui.toggleTheme()"
          >
            <span class="toggle-knob" />
          </button>
          <span class="setting-value">{{ ui.theme === 'light' ? '开' : '关' }}</span>
        </div>
      </div>
      <div class="section">
        <h3>其他</h3>
        <div class="setting-row">
          <span class="setting-label">启动日志覆盖</span>
          <div class="segmented" role="radiogroup" aria-label="启动日志覆盖">
            <button
              v-for="opt in [
                { value: 'auto', label: '跟随游戏' },
                { value: 'off', label: '强制关' },
                { value: 'on', label: '强制开' },
              ]"
              :key="opt.value"
              class="segmented-item"
              :class="{ active: game.displayReportOverride === opt.value }"
              role="radio"
              :aria-checked="game.displayReportOverride === opt.value"
              @click="chooseDisplayReportOverride(opt.value as DisplayReportOverride)"
            >
              {{ opt.label }}
            </button>
          </div>
        </div>
        <p class="hint-text">
          覆盖游戏配置的加载时显示报告（DisplayReport）：跟随游戏=按游戏自身配置；
          强制关=隐藏启动时的脚本读取日志（默认）；强制开=输出逐文件加载报告与
          time.log 阶段计时（排查启动性能用）。当前会话已打印的日志不受切换影响。
        </p>
        <div class="setting-row">
          <span class="setting-label">文件日志 (agent.log)</span>
          <button
            class="toggle"
            :class="{ on: game.agentLogEnabled }"
            role="switch"
            :aria-checked="game.agentLogEnabled"
            @click="toggleAgentLog"
          >
            <span class="toggle-knob" />
          </button>
          <span class="setting-value">{{ game.agentLogEnabled ? '开' : '关' }}</span>
        </div>
        <p class="hint-text">
          输出诊断用文件日志（保存在 app 私有目录下的 agent.log）。默认关闭，
          排查问题时请临时开启。
        </p>
        <div class="setting-row">
          <span class="setting-label">日志查看</span>
          <button class="btn-outline" @click="viewLog">查看最新</button>
          <button class="btn-outline" @click="exportLog">导出</button>
        </div>
        <div v-if="game.agentLogContent" class="log-view">
          <div class="log-view-head">
            <span class="log-view-title">
              {{ game.agentLogTruncated ? '日志（末尾 200K 字符）' : '日志' }}
            </span>
            <button class="btn-outline btn-sm" @click="copyLog">
              {{ logCopied ? '已复制' : '复制' }}
            </button>
          </div>
          <pre class="log-view-body">{{ game.agentLogContent }}</pre>
        </div>
        <p v-else class="hint-text">
          暂无日志。请先开启文件日志并进行操作，再点击「查看最新」。
        </p>
      </div>
    </div>
  </section>
</template>

<style scoped>
.settings-view {
  width: 100%;
  height: 100%;
  background: var(--color-bg);
  overflow-y: auto;
}
.settings-content {
  max-width: 600px;
  margin: 0 auto;
  padding: var(--space-5);
  display: flex;
  flex-direction: column;
  gap: var(--space-5);
}
.section {
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-control);
  overflow: hidden;
}
h3 {
  margin: 0;
  padding: var(--space-2) var(--space-4);
  font-size: var(--font-size-md);
  font-weight: 600;
  color: var(--color-indicator);
  background: var(--color-surface);
  border-bottom: 1px solid var(--color-border);
}
.setting-row {
  display: flex;
  align-items: center;
  padding: var(--space-3) var(--space-4);
  gap: var(--space-3);
  border-bottom: 1px solid var(--color-border);
}
.setting-row:last-child {
  border-bottom: none;
}
.setting-label {
  color: var(--color-text);
  font-size: var(--font-size-md);
  min-width: 120px;
}
.setting-value {
  color: var(--color-text);
  font-size: var(--font-size-base);
  font-weight: 600;
  font-variant-numeric: tabular-nums;
  min-width: 48px;
  text-align: left;
}
.hint-text {
  color: var(--color-text-muted);
  font-size: var(--font-size-sm);
  padding: var(--space-2) var(--space-4) var(--space-3);
  margin: 0;
  line-height: 1.6;
}
/* A0：文件日志开关——暗色主题 toggle，ON 时高亮（primary） */
.toggle {
  position: relative;
  width: 44px;
  height: 24px;
  border-radius: 12px;
  border: none;
  background: var(--color-control);
  cursor: pointer;
  padding: 0;
  transition: background var(--motion-fast), border-color var(--motion-fast);
  flex-shrink: 0;
}
.toggle.on {
  background: var(--color-control-hover);
}
.toggle-knob {
  position: absolute;
  top: 2px;
  left: 2px;
  width: 18px;
  height: 18px;
  border-radius: 50%;
  background: var(--color-text-muted);
  transition: left var(--motion-fast);
}
.toggle.on .toggle-knob {
  left: 22px;
  background: var(--color-text);
}
/* 启动日志覆盖三态——分段控件（radiogroup 语义，激活项高亮） */
.segmented {
  display: inline-flex;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-control);
  overflow: hidden;
  flex-shrink: 0;
}
.segmented-item {
  border: none;
  background: var(--color-surface);
  color: var(--color-text-muted);
  font-size: var(--font-size-sm);
  padding: var(--space-2) var(--space-3);
  cursor: pointer;
  transition: background var(--motion-fast), color var(--motion-fast);
}
.segmented-item + .segmented-item {
  border-left: 1px solid var(--color-border);
}
.segmented-item.active {
  background: var(--color-control-hover);
  color: var(--color-text);
  font-weight: 600;
}
/* A0 补充：查看日志按钮 + 日志展示区（基于 .btn-outline 实心控件，DESIGN.md） */
.log-view {
  border-top: 1px solid var(--color-border);
}
.log-view-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: var(--space-2) var(--space-4);
  background: var(--color-surface);
}
.log-view-title {
  color: var(--color-indicator);
  font-size: var(--font-size-sm);
}
.log-view-body {
  margin: 0;
  padding: var(--space-3) var(--space-4);
  max-height: 320px;
  overflow-y: auto;
  color: var(--color-text);
  font-family: var(--font-mono);
  font-size: var(--font-size-xs);
  line-height: 1.5;
  white-space: pre-wrap;
  word-break: break-all;
  background: var(--color-bg);
}
</style>
