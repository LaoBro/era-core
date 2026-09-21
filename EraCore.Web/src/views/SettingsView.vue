<script setup lang="ts">
import { ref, watch, computed, onMounted, onUnmounted } from 'vue';
import { useGameStore } from '../stores/game';
import { useUiStore } from '../stores/ui';
import { useConnectionStore } from '../stores/connection';
import { isMauiEnvironment, setAgentLogEnabled, setDimLockPolicy, setDisplayReportOverride, setMaxParallelErbFiles, setPerfOverride, setSafReadDegree, setSweepGatePolicy, settingsReset, setSettingsOpen, getAgentLog, exportAgentLog } from '../lib/mauiBridge';
import type { DimLockPolicyValue, DisplayReportOverride, MaxParallelErbFilesValue, PerfOverrideKey, SafReadDegreeValue, SweepGatePolicyValue } from '../lib/mauiBridge';

const game = useGameStore();
const ui = useUiStore();
const conn = useConnectionStore();

// ADR-0030：MAUI 下通知 C# 设置页覆盖层的开关状态——列表页上安卓返回键据此「先关设置页」而非退出 app。
onMounted(() => {
  if (isMauiEnvironment()) setSettingsOpen(true);
});
onUnmounted(() => {
  if (isMauiEnvironment()) setSettingsOpen(false);
});

/**
 * ADR-0030：HTTP 模式 /settings 端点 base（与 useAppInit 的 HTTP 分支同源）。
 * MAUI 模式走 bridge 消息；Web/HTTP 模式走 GET/PUT/DELETE /settings（headless 内存 store）。
 */
const httpBase = computed(() => conn.deriveHttpBase(conn.serverUrl));

/** 关闭设置页——切回 terminal 视图（游戏页显示终端；列表页关闭覆盖层回到游戏选择页）。 */
function closeSettings(): void {
  ui.switchView('terminal');
}

/** ADR-0030：恢复默认——MAUI 走 bridge settingsReset；HTTP 走 DELETE /settings。 */
function resetSettings(): void {
  if (isMauiEnvironment()) {
    settingsReset();
    return;
  }
  void game.resetSettingsHttp(httpBase.value);
}

/**
 * A0（saf-accel 计划）：文件日志开关切换——投递 setAgentLogEnabled 请求 C# 切换 +
 * 持久化，本地乐观更新。C# 处理完后推回 config 消息（含 agentLogEnabled 字段）
 * 再次同步——最终显示以 C# 权威状态为准。
 */
function toggleAgentLog(): void {
  if (isMauiEnvironment()) {
    const next = !game.agentLogEnabled;
    setAgentLogEnabled(next);
    game.agentLogEnabled = next;
    return;
  }
  void game.updateSettingsHttp(httpBase.value, 'agentLogEnabled', game.agentLogEnabled ? 'false' : 'true');
}

/**
 * 启动日志覆盖三态切换——投递 setDisplayReportOverride 请求 C# 覆盖游戏
 * `DisplayReport` + 持久化，本地乐观更新。C# 处理完推回 config 消息
 * （含 displayReportOverride 字段）再次同步——最终显示以 C# 权威状态为准。
 */
function chooseDisplayReportOverride(value: DisplayReportOverride): void {
  if (isMauiEnvironment()) {
    setDisplayReportOverride(value);
    game.displayReportOverride = value;
    return;
  }
  void game.updateSettingsHttp(httpBase.value, 'displayReportOverride', value);
}

/**
 * 性能（启动加载）覆盖三态切换——投递 setPerfOverride 请求 C# 覆盖游戏对应
 * emuera.config 项 + 持久化，本地乐观更新。C# 处理完推回 config 消息（含对应字段）
 * 再次同步——最终显示以 C# 权威状态为准。
 * 注意：这组开关只在 ERB 加载时被引擎读取，本局不受影响，重新进入游戏后生效。
 */
function choosePerfOverride(key: PerfOverrideKey, value: DisplayReportOverride): void {
  if (isMauiEnvironment()) {
    setPerfOverride(key, value);
    game.perfOverrides[key] = value;
    return;
  }
  void game.updateSettingsHttp(httpBase.value, key, value);
}

/** 性能区块的四个开关行——label + wire key，模板内遍历渲染。 */
const perfRows: Array<{ key: PerfOverrideKey; label: string }> = [
  { key: 'lazyFunctionLoad', label: '懒函数加载' },
  { key: 'irDiskCacheLoad', label: 'IR 磁盘缓存' },
  { key: 'irFirstDirList', label: 'IR firstDir 清单（命中轮免目录扫描）' },
  { key: 'irFileListCache', label: 'IR 脚本清单（命中轮免全树枚举）' },
  { key: 'srcContentCache', label: 'CSV/ERH 内容缓存（命中轮免逐文件读）' },
  { key: 'parallelErbLoad', label: '并行读取 ERB' },
  { key: 'parallelParseScript', label: '并行解析未调用函数' },
];

/**
 * ADR-0026 诊断旋钮一（并行未调用遍历平台门策略）切换——投递 setSweepGatePolicy 请求 C#
 * 写 Preferences + 持久化，本地乐观更新。C# 处理完推回 config 消息（含 sweepGatePolicy 字段）
 * 再次同步——最终显示以 C# 权威状态为准。
 * 注意：策略只在 ERB 加载时被读取，本局不受影响，重新进入游戏后生效。
 */
function chooseSweepGatePolicy(value: SweepGatePolicyValue): void {
  if (isMauiEnvironment()) {
    setSweepGatePolicy(value);
    game.sweepGatePolicy = value;
    return;
  }
  void game.updateSettingsHttp(httpBase.value, 'sweepGatePolicy', value);
}

/** 读取并发度——模式（默认/手动）本地 UI 态；选手动时才显示数值输入框。 */
const safMode = ref<'auto' | 'manual'>(game.safReadDegree === 'auto' ? 'auto' : 'manual');
/** 手动并发度输入框（自由数值，C# 侧钳制 1–64）。 */
const safDegreeInput = ref<string>('');
/** 刚应用过的短暂反馈（"已应用 N"），1.5s 后消失。 */
const safDegreeApplied = ref(false);
/** 首次切到手动、尚未设置过数值时的默认初值。 */
const DEFAULT_MANUAL_DEGREE = '4';

/** 同步 C# 权威状态：config 消息推回的 safReadDegree 变化时对齐到本地模式与输入框。
 *  immediate：SettingsView 按 v-if 切换会销毁重建——重挂载时也回填持久化值，避免
 *  手动输入框空白（"设了但看不出来"的反馈缺失）。 */
watch(
  () => game.safReadDegree,
  (v) => {
    safMode.value = v === 'auto' ? 'auto' : 'manual';
    if (v !== 'auto') safDegreeInput.value = v;
  },
  { immediate: true },
);

/**
 * ADR-0026 诊断旋钮二（读取并发度；2026-09-15 起显式值对本地路径同样生效）模式切换——状态始终有清楚反馈：
 * 拨「默认」立即投递并回 auto；拨「手动」显现输入框（反填当前数值，未设置过则填默认），
 * 输入后点「应用」或回车才提交。C# 规范化（数值钳制 1–64）+ 写 Preferences，本地乐观更新，
 * 处理完推回 config 消息（含规范化后的 safReadDegree 字段）再次同步——最终以 C# 权威状态为准。
 * 注意：并发度只在 Preload.Load 时被读取，本局不受影响，重新进入游戏后生效。
 */
function chooseSafMode(mode: 'auto' | 'manual'): void {
  safMode.value = mode;
  safDegreeApplied.value = false;
  if (mode === 'auto') {
    applySafReadDegree('auto');
  } else {
    safDegreeInput.value = game.safReadDegree !== 'auto' ? game.safReadDegree : DEFAULT_MANUAL_DEGREE;
  }
}

/** 投递 setSafReadDegree 请求 C# 规范化 + 写 Preferences；本地乐观更新。 */
function applySafReadDegree(value: SafReadDegreeValue): void {
  if (isMauiEnvironment()) {
    setSafReadDegree(value);
    game.safReadDegree = value;
    return;
  }
  void game.updateSettingsHttp(httpBase.value, 'safReadDegree', value);
}

/** 手动值应用：非正整数忽略；越界按 C# 同款规则钳制 1–64 后投递，并给出"已应用"反馈。 */
function applyCustomSafReadDegree(): void {
  // 注意：<input type="number"> 的 v-model 会把用户输入转成 number（Vue 对 number 输入默认
  // castToNumber），此处统一 String() 归一后再 trim/正则，避免 number.trim() 抛 TypeError
  // 导致第二次及以后的应用静默失败（无投递、无持久化、UI/日志不变）。
  const raw = String(safDegreeInput.value ?? '').trim();
  if (!/^\d+$/.test(raw)) return;
  const clamped = String(Math.min(64, Math.max(1, parseInt(raw, 10))));
  safDegreeInput.value = clamped;
  applySafReadDegree(clamped);
  safDegreeApplied.value = true;
  setTimeout(() => (safDegreeApplied.value = false), 1500);
}

/**
 * ADR-0026 诊断旋钮三（06-K1，并行生成度上限）切换——投递 setMaxParallelErbFiles 请求 C#
 * 词表校验 + 写 Preferences，本地乐观更新。C# 处理完推回 config 消息（含 maxParallelErbFiles
 * 字段）再次同步——最终显示以 C# 权威状态为准。
 * 注意：生成度只在 ERB 加载时被读取，本局不受影响，重新进入游戏后生效。
 */
function chooseMaxParallelErbFiles(value: MaxParallelErbFilesValue): void {
  if (isMauiEnvironment()) {
    setMaxParallelErbFiles(value);
    game.maxParallelErbFiles = value;
    return;
  }
  void game.updateSettingsHttp(httpBase.value, 'maxParallelErbFiles', value);
}

/**
 * ADR-0026 诊断旋钮四（06-K2，privateDimList 锁策略）切换——投递 setDimLockPolicy 请求 C#
 * 词表校验 + 写 Preferences，本地乐观更新。C# 处理完推回 config 消息（含 dimLockPolicy 字段）
 * 再次同步——最终显示以 C# 权威状态为准。
 * 注意：nolock 为纯取证档（关锁可致状态损坏），仅真机单变量取证时短时拨动；
 * 锁策略只在 IdentifierDictionary 构造时被读取，本局不受影响，重新进入游戏后生效。
 */
function chooseDimLockPolicy(value: DimLockPolicyValue): void {
  if (isMauiEnvironment()) {
    setDimLockPolicy(value);
    game.dimLockPolicy = value;
    return;
  }
  void game.updateSettingsHttp(httpBase.value, 'dimLockPolicy', value);
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
      <!-- ADR-0030：设置页是全局覆盖层。MAUI 用安卓返回键关闭（App.vue backButtonPressed 已处理）；
           Web 模式无系统返回键，保留按钮。 -->
      <div class="settings-header">
        <button
          v-if="!isMauiEnvironment()"
          type="button"
          class="btn-outline btn-sm"
          @click="closeSettings"
        >← 返回</button>
        <span class="settings-header-title">设置</span>
      </div>
      <div class="section">
        <h3>全局设置</h3>
        <div class="setting-row">
          <span class="setting-label">恢复默认</span>
          <button class="btn-outline" @click="resetSettings">恢复默认设置</button>
        </div>
        <p class="hint-text">
          把全部全局设置重置为默认（auto/出厂值）。某设置导致进游戏即崩、改不回来时用它一键拨回；
          CLI 侧等效命令：eracore_agent settings reset。
        </p>
      </div>
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
        <div class="setting-row">
          <span class="setting-label">菜单预热</span>
          <button
            class="toggle"
            :class="{ on: ui.menuPrewarm }"
            role="switch"
            :aria-checked="ui.menuPrewarm"
            @click="ui.toggleMenuPrewarm()"
          >
            <span class="toggle-knob" />
          </button>
          <span class="setting-value">{{ ui.menuPrewarm ? '开' : '关' }}</span>
        </div>
        <p class="hint-text">
          首次点开 ⋮ 菜单的卡顿优化：页面空闲时用一个不可见的探针把菜单材质（阴影 / 遮罩 /
          圆角裁剪 / 图标）提前绘制一次，把 GPU shader 编译挪出首次打开的那几帧，让出场动画
          完整播出来。本机开关只作用于前端渲染，不随「恢复默认」重置。默认开；关掉即回到
          优化前基线（首次打开可能丢帧），供低端设备或对比复验使用。关掉不会撤销本会话
          已编译好的 shader，重开应用后才完全回到基线。
        </p>
      </div>
      <div class="section">
        <h3>性能（启动加载）</h3>
        <div v-for="row in perfRows" :key="row.key" class="setting-row">
          <span class="setting-label">{{ row.label }}</span>
          <div class="segmented" role="radiogroup" :aria-label="row.label">
            <button
              v-for="opt in [
                { value: 'auto', label: '跟随游戏' },
                { value: 'off', label: '强制关' },
                { value: 'on', label: '强制开' },
              ]"
              :key="opt.value"
              class="segmented-item"
              :class="{ active: game.perfOverrides[row.key] === opt.value }"
              role="radio"
              :aria-checked="game.perfOverrides[row.key] === opt.value"
              @click="choosePerfOverride(row.key, opt.value as DisplayReportOverride)"
            >
              {{ opt.label }}
            </button>
          </div>
        </div>
        <p class="hint-text">
          覆盖游戏 emuera.config 的启动加载性能开关（仅内存覆盖，不改写游戏配置文件）。
          安卓上若感觉进入游戏或运行中变慢，可将「懒函数加载」切到强制关回到即时解析
          （IR 磁盘缓存仅在懒函数加载开启时生效）；并行两项用于排查加载语义差异。
          切换不影响当前局，重新进入游戏后生效。
        </p>
      </div>
      <div class="section">
        <h3>诊断</h3>
        <div class="setting-row">
          <span class="setting-label">并行未调用遍历平台门</span>
          <div class="segmented" role="radiogroup" aria-label="并行未调用遍历平台门策略">
            <button
              v-for="opt in [
                { value: 'auto', label: '默认' },
                { value: 'forceon', label: '强制并行' },
                { value: 'forceoff', label: '强制串行' },
              ]"
              :key="opt.value"
              class="segmented-item"
              :class="{ active: game.sweepGatePolicy === opt.value }"
              role="radio"
              :aria-checked="game.sweepGatePolicy === opt.value"
              @click="chooseSweepGatePolicy(opt.value as SweepGatePolicyValue)"
            >
              {{ opt.label }}
            </button>
          </div>
        </div>
        <p class="hint-text">
          诊断旋钮，默认保持 auto；复现 Android 并行崩溃用。默认=跟随平台门（Android 上串行，
          行为与旧版本完全一致）；强制并行=Android 上越过平台门进入并行未调用遍历（取证用，
          可能让 app 崩溃）；强制串行=无论任何配置一律串行（紧急杀开关）。仅内存覆盖，
          游戏配置无法解除；切换不影响当前局，重新进入游戏后生效，实际执行路径以日志
          parsescript_policy 的 policy= 字段为准。
        </p>
        <div class="setting-row">
          <span class="setting-label">读取并发度</span>
          <div class="segmented" role="radiogroup" aria-label="读取并发度">
            <button
              class="segmented-item"
              :class="{ active: safMode === 'auto' }"
              role="radio"
              :aria-checked="safMode === 'auto'"
              @click="chooseSafMode('auto')"
            >
              默认
            </button>
            <button
              class="segmented-item"
              :class="{ active: safMode === 'manual' }"
              role="radio"
              :aria-checked="safMode === 'manual'"
              @click="chooseSafMode('manual')"
            >
              手动
            </button>
          </div>
        </div>
        <div v-if="safMode === 'manual'" class="setting-row saf-degree-row">
          <span class="setting-label">并发数值</span>
          <input
            v-model="safDegreeInput"
            class="saf-degree-input"
            type="number"
            min="1"
            max="64"
            placeholder="1-64"
            aria-label="并发数值"
            @keydown.enter="applyCustomSafReadDegree"
          />
          <button class="segmented-item" aria-label="应用并发数值" @click="applyCustomSafReadDegree">
            应用
          </button>
          <span v-if="safDegreeApplied" class="setting-value saf-applied-hint" aria-live="polite">
            已应用 {{ game.safReadDegree }}
          </span>
        </div>
        <p class="hint-text">
          诊断旋钮，默认保持 auto；并发读取取证实验用——拨「手动」可输入 1-64 任意值二分崩溃
          阈值。auto 时按路径类型取值：SAF content-URI 目录=每树 1 并发（与旧版本完全一致），
          本地/桌面文件系统目录=不限并发。填了数值则两种路径都收窄到该并发度（高并发可能触发
          已知并发读取卡死/崩溃）。仅内存覆盖，游戏配置无法设置；切换不影响当前局，重新进入
          游戏后生效，实际并发状态以日志 preload_policy 的 requested=/effective= 字段为准。
        </p>
        <div class="setting-row">
          <span class="setting-label">并行生成度上限</span>
          <div class="segmented" role="radiogroup" aria-label="并行生成度上限">
            <button
              v-for="opt in [
                { value: 'auto', label: '默认' },
                { value: '4', label: '4' },
                { value: '8', label: '8' },
                { value: '16', label: '16' },
              ]"
              :key="opt.value"
              class="segmented-item"
              :class="{ active: game.maxParallelErbFiles === opt.value }"
              role="radio"
              :aria-checked="game.maxParallelErbFiles === opt.value"
              @click="chooseMaxParallelErbFiles(opt.value as MaxParallelErbFilesValue)"
            >
              {{ opt.label }}
            </button>
          </div>
        </div>
        <p class="hint-text">
          诊断旋钮，默认保持 auto；真机并行度/内存峰值取证用。默认=min(核数, 8)（与现状完全
          一致）；数值=同时生成/未合并的 ERB 文件数上限（与未调用遍历清扫度同源同拨，
          显式档不与核数取 min，16 档即超订阅观测点）。仅内存覆盖，游戏配置无法设置；
          切换不影响当前局，重新进入游戏后生效，实际窗口以日志 LoadPendingParallel 的
          knob=/window= 字段为准。
        </p>
        <div class="setting-row">
          <span class="setting-label">privateDim 锁</span>
          <div class="segmented" role="radiogroup" aria-label="privateDimList 锁策略">
            <button
              v-for="opt in [
                { value: 'auto', label: '默认加锁' },
                { value: 'nolock', label: '关锁 ⚠' },
              ]"
              :key="opt.value"
              class="segmented-item"
              :class="{ active: game.dimLockPolicy === opt.value, danger: opt.value === 'nolock' }"
              role="radio"
              :aria-checked="game.dimLockPolicy === opt.value"
              @click="chooseDimLockPolicy(opt.value as DimLockPolicyValue)"
            >
              {{ opt.label }}
            </button>
          </div>
        </div>
        <p class="hint-text" :class="{ 'danger-text': game.dimLockPolicy === 'nolock' }">
          ⚠ 取证专用，可致状态损坏：关锁会重新打开它防的并行加载期 List 并发损坏竞态
          （loading 假警告/异常判别实验用，issue 04-H1）。默认保持加锁（与止血版本完全一致）；
          仅真机单变量取证时短时拨动，实验完立即拨回。仅内存覆盖，游戏配置无法设置；
          切换不影响当前局，重新进入游戏后生效，实际锁状态以日志 parsescript_policy 的
          dimlock= 字段为准。
        </p>
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
/* ADR-0030：设置页返回头——全局覆盖层（列表页/游戏页都可打开）的显式出口 */
.settings-header {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  padding: var(--space-2) var(--space-4);
}
.settings-header-title {
  font-size: var(--font-size-lg);
  font-weight: 600;
  color: var(--color-text);
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
/* 诊断旋钮二：读取并发度自定义输入（自由数值，C# 侧钳制 1–64） */
.saf-degree-input {
  border: none;
  border-left: 1px solid var(--color-border);
  background: var(--color-surface);
  color: var(--color-text);
  font-size: var(--font-size-sm);
  padding: var(--space-2) var(--space-3);
  width: 5.5em;
  outline: none;
}
/* 诊断旋钮二：手动值应用成功的短暂反馈（醒目非文本色） */
.saf-applied-hint {
  color: var(--color-indicator);
}
/* 诊断旋钮四（06-K2）：nolock 取证档显著危险标注——按钮 danger 色描边，激活 danger 色填充 */
.segmented-item.danger {
  color: var(--color-error);
  font-weight: 600;
}
.segmented-item.danger.active {
  /* 双声明：旧 WebView 无 color-mix → 落到纯底色 */
  background: var(--color-bg);
  background: color-mix(in srgb, var(--color-error) 22%, var(--color-surface));
  color: var(--color-error);
}
.danger-text {
  color: var(--color-error);
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
