<script setup lang="ts">
import { ref, computed, watch, nextTick, onMounted, onUnmounted } from 'vue';
import { useGameStore } from '../stores/game';
import { useConnectionStore } from '../stores/connection';
import { useUiStore } from '../stores/ui';
import { isValueInputType } from '../lib/awaiting';

/**
 * InputBar.vue — 输入栏 + 游戏状态显示 + TINPUT 倒计时（ui-redesign-spec §6.3）。
 *
 * ## issue 08：整条分支链从「引擎状态字段」改按 v12 契约 `awaiting` 分派
 *
 * 迁移前是五路整块分支（`state` = WaitInput / Running / Quit / Error / 空 / 未知）。
 * 其中「脚本正在执行」（Running）在 v12 契约里**没有落点**——帧只在脚本停稳时产出，
 * wire 上不存在「脚本正在跑」这一态，该分支已删除（不再保留永不命中的死分支）。
 *
 * 新分派（与 `TerminalView.showInputBar` / `App.keyboardActive` 同源）：
 * 1. `awaiting.ended` 非空 ⇒ 终态行（`terminal` = Quit / Error / 其他）
 * 2. `awaiting.input` 非空 ⇒ 输入 UI，按 `input.type` 再分支：
 *    - `IntValue` / `StrValue`：输入框 + 「发送」，值输入类型禁止空提交
 *    - `AnyValue`：输入框 + 「发送」，允许空提交
 *    - `AnyKey`：状态行「按任意键继续」，不渲染空输入框
 *    - `EnterKey`：状态行「按回车继续」，不渲染空输入框
 *
 * 两路都不命中（`awaiting` 为空 / 只有 `wait`）时**不渲染任何内容**——本组件由
 * `TerminalView.showInputBar` 以「`awaiting.input` 或 `awaiting.ended`」为条件挂载，
 * 故那两种情形根本走不到这里（收缩阶段（09）已删除版本兼容期的「等待连接」兜底分支）。
 *
 * 其余职责不变：
 * - ADR-0016：TINPUT 超时通知 + 实时倒计时（TinputCountdown 组件，输入栏上方窄状态行）。
 * - 输入框聚焦：进入输入等待且为文本类型时自动聚焦。
 * - 虚拟滚动 sticky 守卫——AnyKey 模式下 `isStickyToBottom=false` 时拒绝全局 click 推进。
 *
 * 视觉（spec §6.3）：容器 --color-surface + 顶部 --color-border 分隔线；左侧输入类型标签；
 * 输入框单下边框、聚焦时 --color-indicator 下边框 + 清晰焦点环；提交按钮文案「发送 / 继续」。
 */
const game = useGameStore();
const conn = useConnectionStore();
const ui = useUiStore();

/** 输入框当前值——`v-model` 绑定。每次 turn 切换后清空。 */
const inputValue = ref<string>('');
/** 输入框 DOM 引用——用于 auto-focus。 */
const inputEl = ref<HTMLInputElement | null>(null);

/** 当前输入请求类型——v12 契约派生（`awaiting.input` 缺省即 null）。 */
const inputType = computed<string | null>(() => game.awaiting.input?.type ?? null);

/** 终态取值——v12 契约 `awaiting.ended.terminal`（"Quit" / "Error"）。 */
const terminal = computed<string | null>(() => game.awaiting.ended?.terminal ?? null);

/** 当前是否处于可输入状态——`awaiting.input` 非空（issue 08 前读 `state === 'WaitInput'`）。 */
const canSubmit = computed<boolean>(
  () =>
    conn.status === 'connected'
    && conn.canInput
    && game.awaiting.input != null
    && !game.inputInFlight,
);

/** 当前是否需要值输入——IntValue/StrValue/AnyValue 三类有输入框。 */
const hasTextInput = computed<boolean>(() => {
  const t = inputType.value;
  return t === 'IntValue' || t === 'StrValue' || t === 'AnyValue';
});

/**
 * 是否显示文本输入框——仅文本/数字类型显示实际文本字段（spec §6.3）：
 * AnyKey/EnterKey 手动打开时也保留「按任意键/回车继续」语义，不渲染空输入框。
 */
const showTextField = computed<boolean>(() => hasTextInput.value);

/**
 * 是否禁止空提交——IntValue/StrValue（值输入）禁止空，AnyValue 始终允许空。
 * 值输入判定收敛到 `isValueInputType`（与 store 的 `needValue` 派生同源）。
 */
const blockEmpty = computed<boolean>(() => isValueInputType(inputType.value));

/**
 * 提交当前输入。
 * - 有输入框：取 inputValue，空串 + blockEmpty 时拒绝
 * - 无输入框（AnyKey/EnterKey）：直接提交空串——server 端 inputType 决定是否接受
 * 提交后清空 inputValue；提交前置 inputInFlight 乐观锁防双提交。
 */
function submit(): void {
  if (!canSubmit.value) return;

  if (hasTextInput.value) {
    const v = inputValue.value;
    if (v === '' && blockEmpty.value) return; // 禁止空提交
    game.setInputInFlight();
    conn.sendInput(v);
    inputValue.value = '';
  } else {
    // AnyKey / EnterKey：提交空串
    game.setInputInFlight();
    conn.sendInput('');
  }
}

/**
 * 监听输入请求类型变化——进入新的输入等待（含同一等待内 inputType 改变）时清空输入框
 * 并 auto-focus；离开输入等待（类型变 null）时清空（避免残留）。
 *
 * issue 08：判据从 `displayState.state === 'WaitInput'` 改为契约 `awaiting.input.type`；
 * 原「state 变化」+「inputType 变化」两个 watch 合并为一个（同一维度的两个视图）。
 */
watch(
  () => inputType.value,
  (newType, oldType) => {
    if (newType === oldType) return;
    if (newType !== null) {
      inputValue.value = '';
      if (hasTextInput.value) {
        void nextTick(() => {
          inputEl.value?.focus();
        });
      }
    } else {
      inputValue.value = '';
    }
  },
);

// ---------- AnyKey / EnterKey 全局键盘监听 ----------
// AnyKey：点击页面任意位置或按任意键提交空 input；EnterKey：按 Enter 提交。
// 用 window-level keydown + click 监听，仅在 WaitInput 且 inputType 匹配时挂载。

function isAnyKeyMode(): boolean {
  return (
    canSubmit.value &&
    inputType.value === 'AnyKey'
  );
}

/** 全局 keydown 处理——EnterKey 监听 Enter，AnyKey 监听任意键。 */
function onGlobalKeydown(e: KeyboardEvent): void {
  if (e.ctrlKey || e.metaKey) return;
  if (!canSubmit.value) return;
  const t = inputType.value;
  if (t === 'EnterKey') {
    if (e.key === 'Enter') {
      e.preventDefault();
      submit();
    }
  } else if (t === 'AnyKey') {
    const modifierOnly = ['Shift', 'Control', 'Alt', 'Meta'].includes(e.key);
    if (modifierOnly) return;
    e.preventDefault();
    submit();
  }
}

/** 全局 click 处理——仅 AnyKey 模式下，点击页面任意位置触发提交。
 *  虚拟滚动 sticky 守卫：`isStickyToBottom=false` 时拒绝推进（翻看历史不误触发）；
 *  按钮区域由 `closest('button')` 跳过——按钮走自身 @click。 */
function onGlobalClick(e: MouseEvent): void {
  if (!isAnyKeyMode()) return;
  if (!ui.isStickyToBottom) return;
  const target = e.target as HTMLElement | null;
  if (target && target.closest('button')) return;
  e.preventDefault();
  submit();
}

onMounted(() => {
  window.addEventListener('keydown', onGlobalKeydown);
  window.addEventListener('click', onGlobalClick);
  if (game.awaiting.input != null && hasTextInput.value) {
    void nextTick(() => inputEl.value?.focus());
  }
});

onUnmounted(() => {
  window.removeEventListener('keydown', onGlobalKeydown);
  window.removeEventListener('click', onGlobalClick);
});

/** 输入框 placeholder——按 inputType 提示。 */
const inputPlaceholder = computed<string>(() => {
  const t = inputType.value;
  if (t === 'IntValue') return '输入数字…';
  if (t === 'StrValue') return '输入文本…';
  if (t === 'AnyValue') return '输入文本…';
  return '';
});

/** 输入框 inputmode——IntValue 用 numeric 提示移动端键盘。 */
const inputMode = computed<'text' | 'numeric'>(() =>
  inputType.value === 'IntValue' ? 'numeric' : 'text',
);

/** 错误显示文案——lastTurn.error 优先（协议层错误），其次 lastError（解析层错误）。 */
const errorText = computed<string | null>(() => {
  if (game.lastTurn?.error) return game.lastTurn.error;
  if (game.lastError) return game.lastError;
  return null;
});

/** 提交按钮 disabled——空 + blockEmpty 时禁用。 */
const submitDisabled = computed<boolean>(() => {
  if (!canSubmit.value) return true;
  if (!hasTextInput.value) return false; // AnyKey/EnterKey 始终可点
  return inputValue.value === '' && blockEmpty.value;
});

/**
 * 提交按钮文案（spec §6.3）：文本/数字/任意值 → 「发送」；AnyKey/EnterKey → 「继续」。
 * 不再使用「提交（可空）」等实现性文案。
 */
const submitLabel = computed<string>(() => {
  const t = inputType.value;
  if (t === 'AnyKey' || t === 'EnterKey') return '继续';
  return '发送';
});

</script>

<template>
  <div class="input-bar">
    <!-- TINPUT 超时通知（输入栏上方窄状态行，spec §6.3） -->
    <div v-if="game.timeoutNotice" class="tinput-notice">
      <span class="tinput-icon" aria-hidden="true">⏱</span>
      <span>{{ game.timeoutNotice }}</span>
    </div>

    <!-- 状态显示：按 v12 契约 awaiting 分派（issue 08） -->

    <!-- 1. 终态：awaiting.ended（terminal = Quit / Error / 其他兜底） -->
    <template v-if="game.awaiting.ended">
      <div v-if="terminal === 'Quit'" class="state-quit">
        <span class="state-icon" aria-hidden="true">■</span>
        <span>游戏结束</span>
      </div>

      <div v-else-if="terminal === 'Error'" class="state-error">
        <span class="state-icon" aria-hidden="true">✕</span>
        <span>错误：{{ errorText ?? '未知错误' }}</span>
      </div>

      <div v-else class="state-unknown">
        <span>状态：{{ terminal }}</span>
      </div>
    </template>

    <!-- 2. 等待输入：awaiting.input 非空，按 input.type 再分支 -->
    <template v-else-if="game.awaiting.input">
      <!-- 文本/数字输入：占位提示 + 输入框 + 发送按钮 -->
      <div v-if="showTextField" class="input-row">
        <input
          ref="inputEl"
          v-model="inputValue"
          class="text-input"
          type="text"
          :inputmode="inputMode"
          :placeholder="inputPlaceholder"
          :disabled="!canSubmit"
          @keyup.enter="submit"
        />
        <button
          class="btn-primary submit-btn"
          :disabled="submitDisabled"
          @click="submit"
        >
          {{ submitLabel }}
        </button>
      </div>

      <!-- AnyKey / EnterKey：状态行 + 「继续」按钮，不渲染空输入框（spec §6.3）。
           按钮提交空串；全局 click 监听已通过 closest('button') 排除按钮自身，不会双提交。 -->
      <div v-else-if="inputType === 'AnyKey'" class="key-prompt">
        <span>按任意键继续</span>
        <button
          class="btn-primary submit-btn"
          :disabled="submitDisabled"
          @click="submit"
        >
          {{ submitLabel }}
        </button>
      </div>

      <div v-else-if="inputType === 'EnterKey'" class="key-prompt">
        <span>按回车继续</span>
        <button
          class="btn-primary submit-btn"
          :disabled="submitDisabled"
          @click="submit"
        >
          {{ submitLabel }}
        </button>
      </div>

      <div v-else class="unknown-input-type">
        <span>未知输入类型：{{ inputType ?? '(null)' }}</span>
      </div>
    </template>

  </div>
</template>

<style scoped>
.input-bar {
  flex-shrink: 0;
  padding: var(--space-1) var(--space-3);
  /* 双声明：env 不可用（旧 WebView）时落到固定 padding */
  padding-bottom: var(--space-1);
  padding-bottom: calc(var(--space-1) + env(safe-area-inset-bottom));
  background: var(--color-surface);
  border-top: none;
  font-family: var(--font-ui);
  font-size: var(--font-size-base);
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
  min-height: 40px;
}

/* TINPUT 超时通知——warning 语义窄状态行 */
.tinput-notice {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-1) var(--space-2);
  /* 双声明：旧 WebView 无 color-mix → 落到纯底色 */
  background: var(--color-bg);
  background: color-mix(in srgb, var(--color-warning) 12%, var(--color-surface));
  color: var(--color-warning);
  border-radius: var(--radius-control);
  border: none;
  font-size: var(--font-size-sm);
}
.tinput-icon {
  font-size: 14px;
}

/* 输入行：input + 发送按钮 */
.input-row {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  min-height: var(--touch-target);
}
/* 浅色圆角输入条：不使用下划线或聚焦高亮。 */
.text-input {
  flex: 1;
  background: var(--color-control);
  color: var(--color-text);
  border: none;
  padding: 6px 14px;
  border-radius: var(--radius-control);
  /* Input prompts are UI chrome, not game output. */
  font-family: var(--font-ui);
  font-size: var(--font-size-base);
  min-height: 40px;
  min-width: 0;
  transition: background-color var(--motion-fast);
}
/* 聚焦仅微调背景（DESIGN.md：无下划线 / 无边框 / 无聚焦高亮）；
   键盘焦点由全局 :focus-visible 焦点环提供（DESIGN.md Accessibility）。 */
.text-input:focus {
  background: var(--color-control-hover);
}
.text-input:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.key-prompt {
  color: var(--color-text-muted);
  padding: var(--space-2) 0;
  min-height: var(--touch-target);
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
}

/* 发送 / 继续按钮使用全局标准文字规格，并与输入框保持 40px 高度。 */
.submit-btn {
  min-width: 0;
  min-height: 40px;
  font-size: var(--font-size-base);
}

/* 状态显示 */
.state-quit,
.state-error,
.state-unknown,
.unknown-input-type {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  color: var(--color-text-muted);
  padding: var(--space-1) 0;
  min-height: 24px;
}
.state-icon {
  font-size: 14px;
}
.state-quit .state-icon {
  color: var(--color-error);
}
.state-error .state-icon {
  color: var(--color-error);
}
.state-error {
  color: var(--color-error);
}
.unknown-input-type {
  color: var(--color-warning);
}
</style>

<!-- 输入栏出现/消失过渡：淡入 + 上浮/下沉，位移与整套浮层统一（--fx-rise=12px）。
     进场用 --fx-ease-in，退场用 --fx-ease-out。 -->
<style>
/* 输入栏作为终端底部覆盖层（overlay-bottom）时的可靠定位——absolute 盖在最上、
   不参与文档流，从而不顶开游戏文本。非 scoped 兜底，避免生效依赖父级 scoped 传导。 */
.input-bar.overlay-bottom {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
}
.inputbar-enter-active {
  transition: opacity var(--motion-slow) var(--fx-ease-in), transform var(--motion-slow) var(--fx-ease-in);
}
.inputbar-leave-active {
  transition: opacity var(--motion-slow) var(--fx-ease-out), transform var(--motion-slow) var(--fx-ease-out);
}
.inputbar-enter-from {
  opacity: 0;
  transform: translateY(var(--fx-rise));
}
.inputbar-leave-to {
  opacity: 0;
  transform: translateY(var(--fx-rise));
}
</style>
