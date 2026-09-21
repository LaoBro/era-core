<script setup lang="ts">
import { onMounted, onUnmounted, ref, computed, watch } from 'vue';
import { useUiStore } from './stores/ui';
import { useGameStore } from './stores/game';
import { useConnectionStore } from './stores/connection';
import { initAppState } from './composables/useAppInit';
import { prewarmMenuShadow } from './lib/prewarmMenuShadow';
import { startGameStatusMonitor } from './composables/useGameStatusMonitor';
import { isMauiEnvironment, loadGameFromPath, exitGame as exitGameBridge } from './lib/mauiBridge';
import AppShell from './components/AppShell.vue';
import DrawerMenu from './components/DrawerMenu.vue';
import type { PopupMenuItem } from './components/menuTypes';
import ConfirmDialog from './components/ConfirmDialog.vue';
import MauiGameList from './components/MauiGameList.vue';
import WebGameList from './components/WebGameList.vue';
import TerminalView from './views/TerminalView.vue';
// DebugView / SettingsView 必须静态导入（原为 defineAsyncComponent(() => import(...))）。
// 保留动态 import() 会让 Vite 注入 __vitePreload(..., import.meta.url)；而 build.target=chrome61 时
// esbuild 会把 import.meta 清空成 {}（"import.meta is not available in the configured target
// environment and will be empty"），于是预载助手 new URL(dep, undefined) 抛 Invalid URL
// → 这两个视图在**所有**平台（含 Windows WebView2）都打不开，且构建期不报错。
// 同时动态 import() 本身要 Chrome 63、import.meta 要 Chrome 64，均在 61 之上。
import DebugView from './views/DebugView.vue';
import SettingsView from './views/SettingsView.vue';

const ui = useUiStore();
const game = useGameStore();
const conn = useConnectionStore();

function onControlHotkey(e: KeyboardEvent): void {
  if (!(e.ctrlKey || e.metaKey) || e.key.toLowerCase() !== 't') return;
  if (isMauiEnvironment() || !conn.isSpectator) return;
  e.preventDefault();
  void conn.acquireControl();
}

/**
 * T-025 D9 rev：App 挂载初始化——逻辑提取到 initAppState() 便于单测。
 * MAUI 额外启动游戏静默监控（useGameStatusMonitor）。
 */
onMounted(() => {
  initAppState();
  startGameStatusMonitor();
  window.addEventListener('keydown', onControlHotkey);
  // 初始化时同步主题到 data-theme
  document.documentElement.dataset.theme = ui.theme;
  // 首次弹出菜单卡顿优化：空闲期预热菜单阴影的 GPU shader（见 spec .scratch/menu-first-open-jank）。
  // 开关在设置页「外观 · 菜单预热」：关 = 不预热，回到优化前基线（低端设备 / 对比复验用）。
  if (ui.menuPrewarm) prewarmMenuShadow();
});

onUnmounted(() => {
  window.removeEventListener('keydown', onControlHotkey);
});

// 同步主题到 <html data-theme>
watch(() => ui.theme, (t) => {
  document.documentElement.dataset.theme = t;
});

/**
 * 菜单预热开关（设置页「外观 · 菜单预热」）：拨到「开」时补一次空闲期预热——当次会话立即生效，
 * 不必重开应用；prewarmMenuShadow 本身幂等（进程内只做一次）。
 * 拨到「关」无法撤销：已编译的 shader 是进程级缓存，故只影响下次启动——该语义在设置页 hint 里说明。
 */
watch(() => ui.menuPrewarm, (on) => {
  if (on) prewarmMenuShadow();
});

/**
 * ADR-0030：从游戏选择页打开设置时，游戏被加载（含 agent 经 CLI 加载）→ 自动关设置页回到游戏画面。
 * 判据用 serverState 的 Idle→非 Idle 跃迁而非 gameDir：MAUI 加载流（layout 消息）只改 serverState，
 * Vue store 的 gameDir 在 MAUI 恒为 null，挂 gameDir 的 watch 在 MAUI 永不触发。
 * `prev === 'Idle'` 守卫：游戏页内开设置后热切换新游戏（非 Idle→非 Idle）不触发，避免打断正在看的设置。
 */
watch(
  () => game.serverState,
  (state, prev) => {
    if (prev === 'Idle' && state !== 'Idle' && ui.currentView === 'settings') {
      ui.switchView('terminal');
    }
  },
);

const isMaui = isMauiEnvironment();

/**
 * MAUI 全屏游戏选择界面可见性——游戏未加载时（gameDir 为 null + serverState 空闲）
 * 显示全屏 MauiGameList；游戏运行中或 HTTP 模式显示标准 AppShell 布局。
 */
const showMauiGameList = computed(() =>
  isMaui && !game.gameDir && game.serverState === 'Idle',
);

/**
 * Web 全屏游戏选择界面可见性——HTTP 模式未加载游戏 + server 空闲时显示 WebGameList
 * （server 代扫 + 目录浏览的游戏选择页）。前端与 server 绑定，无需连接配置。
 */
const showWebGameList = computed(() =>
  !isMaui && !game.gameDir && game.serverState === 'Idle',
);

/** 「快速重开」可见性：HTTP 非 Idle / MAUI 已选目录。 */
const canQuickRestart = computed(() =>
  isMaui ? !!game.gameDir : game.serverState !== 'Idle',
);
const isRestarting = computed(() => game.reloadStatus === 'loading');

/** 「退出游戏」可见性——游戏页（非选择页）显示。 */
const canExitGame = computed(
  () => game.serverState !== 'Idle' || !!game.gameDir,
);
const isExiting = computed(() => game.exitStatus === 'exiting');

/** 退出确认对话框可见性。 */
const showExitConfirm = ref(false);

/** 游戏页「更多」（⋮）抽屉菜单（DrawerMenu）展开状态。 */
const showFloatMenu = ref(false);

/** Android 物理返回键（backButtonPressed 由 C# 投递）——层级语义（spec §6.1）：
 *  抽屉打开 → 关闭抽屉；退出确认打开 → 取消；游戏运行中 → 弹退出确认。 */
watch(() => game.backButtonPressedTick, () => {
  if (!isMaui) return;
  if (showFloatMenu.value) {
    showFloatMenu.value = false;
    return;
  }
  if (showExitConfirm.value) {
    onExitCancel();
    return;
  }
  // ADR-0030：设置页（游戏选择页或游戏页打开）——返回键先关设置
  if (ui.currentView === 'settings') {
    ui.switchView('terminal');
    return;
  }
  if (isExiting.value) return;
  if (game.serverState === 'Idle' && !game.gameDir) return;
  showExitConfirm.value = true;
});

/** 快速重开——HTTP 走 game.quickRestart()；MAUI 投递 loadGameFromPath。 */
async function onQuickRestart(): Promise<void> {
  if (isRestarting.value) return;
  if (!isMaui && !conn.canMutateLifecycle) return;
  if (isMaui) {
    if (!game.gameDir) return;
    game.reset();
    loadGameFromPath(game.gameDir);
    return;
  }
  await game.quickRestart();
}

/** 点击「退出游戏」——弹确认对话框，不直接退出。 */
function onExitClick(): void {
  if (isExiting.value) return;
  showExitConfirm.value = true;
}

/** 确认退出——MAUI 投递 exitGame 消息（C# Dispose + 重建 host）；Web 走 HTTP 退出。 */
async function onExitConfirm(): Promise<void> {
  showExitConfirm.value = false;
  if (isMaui) {
    if (!game.beginExitGame()) return; // 二次进入保护
    exitGameBridge();
    return;
  }
  await game.exitGameHttp();
}

/** 取消退出——关闭对话框，无副作用。 */
function onExitCancel(): void {
  showExitConfirm.value = false;
}

/**
 * spec §6.1：⌨ 键盘按钮 active 状态必须与实际输入栏状态一致，不得只按手动开关表现。
 * 与 TerminalView.showInputBar 同语义：手动唤出时 WaitInput 即显示；
 * 否则当前回合无按钮时显示（文本→输入行；AnyKey/EnterKey→提示行）。
 */
const keyboardActive = computed<boolean>(() => {
  const st = game.displayState.state;
  if (st !== 'WaitInput') return false;
  if (ui.manualInputVisible) return true;
  return !game.hasActiveButtons;
});

/** 「更多」菜单项（MAUI 游戏页）——快速重开、缩放、视图切换、退出（spec §6.1）。 */
const popupItems = computed<PopupMenuItem[]>(() => [
  {
    type: 'item',
    id: 'restart',
    label: isRestarting.value ? '重开中…' : '快速重开',
    icon: 'restart',
    disabled: !canQuickRestart.value || isRestarting.value,
    onClick: () => {
      void onQuickRestart();
    },
  },
  {
    type: 'zoom',
    percent: Math.round(game.effectiveScale * 100),
    canZoomOut: game.isMinScale,
    canZoomIn: game.isMaxScale,
    onZoomOut: () => game.setScale(game.effectiveScale - 0.1),
    onZoomReset: () => game.setScale(1),
    onZoomIn: () => game.setScale(game.effectiveScale + 0.1),
  },
  { type: 'separator' },
  {
    type: 'item',
    id: 'terminal',
    label: 'Terminal',
    icon: 'terminal',
    active: ui.currentView === 'terminal',
    onClick: () => {
      ui.switchView('terminal');
    },
  },
  {
    type: 'item',
    id: 'debug',
    label: 'Debug',
    icon: 'debug',
    active: ui.currentView === 'debug',
    onClick: () => {
      ui.switchView('debug');
    },
  },
  {
    type: 'item',
    id: 'settings',
    label: 'Settings',
    icon: 'settings',
    active: ui.currentView === 'settings',
    onClick: () => {
      ui.switchView('settings');
    },
  },
  { type: 'separator' },
  {
    type: 'item',
    id: 'exit',
    label: isExiting.value ? '退出中…' : '退出游戏',
    icon: 'exit',
    danger: true,
    disabled: !canExitGame.value || isExiting.value,
    onClick: () => {
      onExitClick();
    },
  },
]);
</script>

<template>
  <AppShell :scrollable="showMauiGameList || showWebGameList">
    <!-- 页面过渡动画：out-in 模式，进场 --fx-ease-in，退场 --fx-ease-out -->
    <Transition name="page" mode="out-in">
      <MauiGameList v-if="showMauiGameList" key="game-list" />
      <WebGameList v-else-if="showWebGameList" key="web-game-list" />
      <div v-else class="game-view-wrapper" key="game-view">
        <!-- 全屏游戏页：两个常驻无边框图标（spec §6.1），fixed 不占布局。
             DOM 顺序在 main 之前——满足 §9 Tab 顺序「更多 → 主要内容」。
             MAUI 与 Web 共用同一壳层——除更改目录行为外 UI 与 MAUI 一致。 -->
        <div class="game-shell-controls">
          <button
            class="icon-btn"
            :class="{ active: keyboardActive }"
            aria-label="手动输入"
            title="手动输入"
            @click="ui.toggleManualInput()"
          >⌨</button>
          <button
            class="icon-btn"
            :class="{ active: showFloatMenu }"
            aria-label="更多操作"
            title="更多操作"
            @mousedown.stop
            @click="showFloatMenu = !showFloatMenu"
          >⋮</button>
        </div>

        <!-- 抽屉用 v-show（display:none 切换）：组件随 App 常驻挂载、展开才经 Transition 显示。
             首次打开性能由空闲期预热探针（prewarmMenuShadow.ts）覆盖①。见 spec。 -->
        <Transition name="drawer">
          <DrawerMenu
            v-show="showFloatMenu"
            :visible="showFloatMenu"
            :items="popupItems"
            :title="game.lastPlayedGame || game.gameDir || '更多操作'"
            @close="showFloatMenu = false"
          />
        </Transition>

        <main class="app-main">
          <!-- TerminalView 常驻不卸载——视图切换只切可见性。
               原先三条 v-if/v-else-if/v-else：进 Debug/Settings 会卸载 TerminalView →
               TerminalDisplay 一并销毁 → useVirtualScroll 的 scrollTop/isStickyToBottom 与
               .terminal 滚动容器全部丢失，返回时是新元素、scrollTop 从 0 开始（表现为
               "自动跳回最上方"）；且重建后 vs.isStickyToBottom 回到初值 true，与
               ui.isStickyToBottom（InputBar 点击推进守卫读它）失同步。
               用 visibility 切换而非 v-show/display:none：后者销毁布局盒，scrollTop 与
               scrollHeight 归零，被覆盖期间到达的新回合会经 watch(itemCount) 把位置写成 0。
               visibility:hidden 保留布局盒（滚动位置保留、后台贴底跟随照常）且退出命中测试与 Tab 序。 -->
          <TerminalView :class="{ 'view-hidden': ui.currentView !== 'terminal' }" />
          <!-- Debug 改为覆盖层（absolute inset:0）——根元素 width/height:100% + 自带背景与滚动，
               改绝对定位不改变其内部布局。仍用 v-if：没有需要跨切换保留的滚动状态，离开即卸载。 -->
          <DebugView v-if="ui.currentView === 'debug'" class="view-overlay" />
        </main>
      </div>
    </Transition>

    <!-- ADR-0030：设置全局覆盖层——游戏选择页（列表）与游戏页都可打开（⋮ 菜单「设置」）。
         固定在 AppShell 顶层（fixed inset:0），盖在当前页面之上；关闭经返回头或 Android 返回键。 -->
    <SettingsView v-if="ui.currentView === 'settings'" class="settings-overlay" />

    <!-- 退出确认（Android 风格 Alert Dialog，spec §6.1）——挂载于 AppShell #dialogs -->
    <template #dialogs>
      <ConfirmDialog
        :visible="showExitConfirm"
        title="退出游戏？"
        message="退出后未保存的进度将丢失。"
        confirm-label="退出"
        cancel-label="取消"
        @confirm="onExitConfirm"
        @cancel="onExitCancel"
      />
    </template>

    <!-- 游戏静默状态提示（MAUI）——纯展示、pointer-events:none 不挡交互 -->
    <div
      v-if="isMaui && game.gameStatusHint !== null"
      class="status-hint"
      :class="game.gameStatusHint"
    >
      {{ game.gameStatusHint === 'running' ? '游戏运行中…' : '游戏已停止' }}
    </div>
  </AppShell>
</template>

<style scoped>
.app-main {
  flex: 1;
  overflow: hidden;
  display: flex;
  flex-direction: column;
  /* Debug / Settings 覆盖层（.view-overlay）的定位基准 */
  position: relative;
}

/* Debug / Settings 覆盖层——叠在常驻的 TerminalView 之上。
   终端被覆盖时整棵子树 visibility:hidden（不绘制），故无需与终端内部 z-index
   （最高为输入栏的 40）竞争，这里的正 z-index 只为确保位于文档序之上。 */
.view-overlay {
  position: absolute;
  inset: 0;
  z-index: 2;
}

/* ADR-0030：设置全局覆盖层（fixed inset:0，盖在列表页/游戏页之上）。
   z-index 100：高于游戏页悬浮控件（.game-shell-controls z-50），低于抽屉菜单（899/900）。 */
.settings-overlay {
  position: fixed;
  inset: 0;
  z-index: 100;
  background: var(--color-bg);
}

/* 被覆盖的终端：保留布局盒（scrollTop/scrollHeight 有效、滚动位置不丢、
   隐藏期间新回合的贴底跟随照常），同时隐藏绘制并退出命中测试与 Tab 序。
   禁止改用 display:none（销毁布局盒 → 滚动位置与 scrollHeight 归零）。 */
.view-hidden {
  visibility: hidden;
}

/* 通用图标按钮（spec §3.3）：方形触控目标、无边框、无背景 */
.icon-btn {
  width: var(--touch-target);
  height: var(--touch-target);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: transparent;
  color: var(--color-text);
  border: none;
  border-radius: var(--radius-control);
  cursor: pointer;
  font-size: 18px;
  font-family: var(--font-ui);
  transition: background var(--motion-fast), color var(--motion-fast);
}
.icon-btn:hover:not(:disabled) {
  background: var(--color-surface-raised);
}
.icon-btn:active:not(:disabled) {
  background: var(--color-surface);
}
.icon-btn.active {
  color: var(--color-indicator);
}
.icon-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

/* 全屏游戏页：两个常驻无边框图标（spec §6.1）——透明度一致、固定位置、≥48px 点击区 */
.game-shell-controls {
  position: fixed;
  /* 双声明：env 不可用（旧 WebView）时落到固定 top */
  top: var(--space-2);
  top: calc(var(--space-2) + env(safe-area-inset-top));
  right: var(--space-2);
  display: flex;
  gap: var(--space-1);
  z-index: 50;
}
.game-shell-controls .icon-btn {
  border-radius: 9999px;
  background: transparent;
  color: var(--term-color-text);
}
.game-shell-controls .icon-btn:hover:not(:disabled),
.game-shell-controls .icon-btn:active:not(:disabled) {
  background: var(--color-control-hover);
  color: var(--term-color-text);
}
.game-shell-controls .icon-btn.active {
  color: var(--term-color-text);
}

/* 游戏静默状态提示（MAUI）——顶部半透明窄条，pointer-events:none */
.status-hint {
  position: fixed;
  /* 双声明：env 不可用（旧 WebView）时落到固定 top */
  top: var(--space-3);
  top: calc(var(--space-3) + env(safe-area-inset-top));
  left: 50%;
  transform: translateX(-50%);
  z-index: 60;
  padding: var(--space-1) var(--space-3);
  border-radius: var(--radius-control);
  font-size: var(--font-size-sm);
  color: var(--color-text);
  /* 双声明：color-mix 不可用（旧 WebView）时落到纯底色 */
  background: var(--color-bg);
  background: color-mix(in srgb, var(--color-surface) 80%, transparent);
  border: none;
  pointer-events: none;
  white-space: nowrap;
}
.status-hint.stopped {
  color: var(--color-error);
}

/* 游戏视图包裹层——在 AppShell flex 布局中撑满剩余空间，与 MauiGameList 平级 */
.game-view-wrapper {
  display: flex;
  flex-direction: column;
  flex: 1;
  overflow: hidden;
}

/* 页面过渡动画：进场用 --fx-ease-in，退场用 --fx-ease-out */
.page-enter-active {
  transition: opacity var(--page-duration) var(--fx-ease-in),
              transform var(--page-duration) var(--fx-ease-in);
}
.page-leave-active {
  transition: opacity var(--page-duration) var(--fx-ease-out),
              transform var(--page-duration) var(--fx-ease-out);
}
.page-enter-from {
  opacity: 0;
  transform: scale(var(--page-scale));
}
.page-leave-to {
  opacity: 0;
  transform: scale(var(--page-scale));
}
</style>
