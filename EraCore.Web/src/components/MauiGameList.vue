<script setup lang="ts">
import { ref, computed, watch, onMounted, onUnmounted } from 'vue';
import { useGameStore } from '../stores/game';
import { useUiStore } from '../stores/ui';
import { mauiGameLibrarySource, isAndroidMauiEnvironment } from '../lib/gameLibrary';
import {
  checkAllFilesAccess,
  requestAllFilesAccess,
  pickSafDirectory,
} from '../lib/mauiBridge';
import GameLibraryView from './GameLibraryView.vue';
import PopupMenu, { type PopupMenuItem } from './PopupMenu.vue';

/**
 * MauiGameList — MAUI 游戏选择页（PickerShell）。
 *
 * 只是 GameLibraryView（与 Web 共用一份设计）的薄 wrapper——平台差异在 ⋮ 菜单与空态动作：
 * - 数据获取（扫描/加载）经 mauiGameLibrarySource（C# 桥接）
 * - 「更改目录…」Android 走**系统选择器**（唯一入口），Windows 走原生 FolderPicker
 * - 「所有文件访问权限」（Android，恒显示）快速轨的开关，右侧直显状态；授权后自动重扫以升级
 * - 空态只讲「选目录」一件事；授权退居为可选的加速入口
 *
 * 设计取向（issues/15 方案 B，2026-09-20）：**入口只有一个，轨由判定决定**。
 * 系统选择器按平台契约只产出 content://，但 C# 侧会尝试把它反推成真实路径
 * （externalstorage provider 的 documentId 带相对路径 + 卷挂载点）；
 * 全过走快速轨、任一不过留在 SAF 兼容轨。于是用户不必在选之前做技术选择——
 * 这与 2026-09-11 的「把两条路的代价写进标签、让用户自己选」是取向反转：
 * 那条裁定是**页内浏览器的替代品**，而页内浏览器本身已被系统选择器取代。
 */
const game = useGameStore();
const ui = useUiStore();

/** 顶部 ⋮ 菜单展开状态。 */
const showMenu = ref(false);

/** issues/15 S3：Android MAUI——目录模式与授权相关入口只在此平台出现
 *  （Windows 恒真实路径且无授权概念，加这些项只是噪音）。 */
const isAndroid = computed(() => isAndroidMauiEnvironment());

/**
 * issues/15（方案 B）：Android 只有**一个**目录入口——系统选择器（`pickSafDirectory`）。
 * 它按平台契约只产出 content://，能不能落到真实路径（快速轨）由 C# 侧判定：
 * 全过就用真实路径，任一不过就留在 SAF（兼容轨）。故前端不再有「两条轨两个按钮」的分工，
 * 也不需要「先授权才能选目录」——授权只影响选完之后走哪条轨，不影响能不能选。
 */

/** issues/15：从系统设置页返回 app 后复查授权状态（visibilitychange 触发一次）。 */
function onVisibilityRecheck(): void {
  if (!document.hidden) {
    document.removeEventListener('visibilitychange', onVisibilityRecheck);
    checkAllFilesAccess();
  }
}

/** issues/15：请求授权——跳系统设置页，返回时自动复查。 */
function onRequestAllFilesAccess(): void {
  document.addEventListener('visibilitychange', onVisibilityRecheck);
  requestAllFilesAccess();
}

/**
 * issues/15（方案 B）：授权回流后**重扫一次**的待办意图。
 *
 * 授权要跳系统设置页、结果异步回流，没法同步串联。而权限正是快速轨的闸门——
 * 授权后必须重扫，C# 的归一化才有机会把 content:// 升级成真实路径（快速模式）。
 * 不重扫的话用户会停在兼容轨，还得手动「重新扫描游戏」，等于白授权。
 */
const pendingRescanAfterGrant = ref(false);

/** issues/15（方案 B）：去授权——完成后自动重扫以升级到快速轨。 */
function onGrantForFastMode(): void {
  pendingRescanAfterGrant.value = true;
  onRequestAllFilesAccess();
}

/** issues/15（方案 B）：空态主按钮——直接弹系统选择器（不需要任何权限）。
 *
 *  先置 `scanning`：选完之后 C# 会直接扫库并推 gamesScanned，前端没有第二个可挂加载态的时机。
 *  取消 / 失败由 useAppInit 收回（否则会永远停在「正在扫描…」）。 */
function onPickDirectory(): void {
  game.clearMauiError();
  game.clearLoadGameError();
  game.scanStatus = 'scanning';
  pickSafDirectory();
}

/** issues/15（方案 B）：以当前主目录重扫——授权回流后触发，让 C# 归一化升级到快速轨。 */
function rescanCurrentDir(): void {
  const dir = game.scanRootDir ?? game.mainGameDir;
  if (dir) void mauiGameLibrarySource.scan(dir);
}

/** 授权状态回流后兑现待办意图（见 pendingRescanAfterGrant）。 */
watch(
  () => game.allFilesAccess,
  (granted) => {
    if (!pendingRescanAfterGrant.value) return;
    pendingRescanAfterGrant.value = false;
    if (granted === true) rescanCurrentDir();
  },
);

onMounted(() => {
  // issues/15 S3：Android 恒查一次授权状态——菜单项与空态文案都要据此渲染。
  // 不能再像 S2 那样只在 SAF 模式下查（legacy 模式下的状态会陈旧）。
  if (isAndroid.value) checkAllFilesAccess();
});

onUnmounted(() => {
  document.removeEventListener('visibilitychange', onVisibilityRecheck);
  pendingRescanAfterGrant.value = false;
});

/** 目录操作菜单项——扫描中禁用；点击后由组件自动关闭。 */
const dirMenuItems = computed<PopupMenuItem[]>(() => [
  {
    type: 'item',
    id: 'change-dir',
    label: isScanning.value ? '扫描中…' : '更改目录...',
    // issues/15（方案 B）：Android 只有这一条路（系统选择器），且**不再承诺模式**——
    // 快速/兼容由选完后的判定决定，写「快速模式」会出现承诺与落点不一致。
    ...(isAndroid.value ? { hint: '系统选择器' } : {}),
    disabled: isScanning.value,
    onClick: onChangeMainDir,
  },
  // issues/15（方案 B）：授权项**恒显示**，措辞与模式挂钩——它现在读作「快速模式的开关」。
  // 授权后会自动重扫以升级到快速轨（见 pendingRescanAfterGrant），不是只改个状态。
  ...(isAndroid.value
    ? [
        {
          type: 'item' as const,
          id: 'all-files-access',
          label: '所有文件访问权限',
          hint: game.allFilesAccess === true ? '已授权，快速模式' : '未授权，仅兼容模式',
          onClick: onGrantForFastMode,
        },
      ]
    : []),
  {
    type: 'item',
    id: 'rescan',
    label: '重新扫描游戏',
    disabled: isScanning.value,
    onClick: onRescan,
  },
  { type: 'separator' },
  {
    type: 'item',
    id: 'theme',
    icon: ui.theme === 'dark' ? 'sun' : 'moon',
    label: ui.theme === 'dark' ? '亮色主题' : '暗色主题',
    onClick: () => ui.toggleTheme(),
  },
  // ADR-0030：游戏选择页设置入口——全局设置（不进游戏也能改，崩溃恢复主通道）
  {
    type: 'item',
    id: 'settings',
    icon: 'settings',
    label: '设置',
    onClick: () => ui.switchView('settings'),
  },
]);

/** 当前是否扫描中——菜单项禁用依据。 */
const isScanning = computed(() => game.scanStatus === 'scanning');

/**
 * 「更改目录…」——Android 走**系统选择器**（issues/15 方案 B 起唯一入口），
 * Windows 保持原生 FolderPicker。两者都是系统原生 UI，语义对齐。
 */
function onChangeMainDir(): void {
  game.clearMauiError();
  if (isAndroid.value) {
    onPickDirectory();
    return;
  }
  void mauiGameLibrarySource.pickMainDir();
}

/** 重新扫描——以当前主目录为起点重扫（未选目录时退化为更改目录）。 */
function onRescan(): void {
  const dir = game.scanRootDir ?? game.mainGameDir;
  if (!dir) {
    onChangeMainDir();
    return;
  }
  game.setMainGameDir(dir);
  void mauiGameLibrarySource.scan(dir);
}
</script>

<template>
  <GameLibraryView :source="mauiGameLibrarySource">
    <!-- issues/15（方案 B）：Android 的空态只讲一件事——选目录。走哪条轨是选完之后自动判定的，
         不该让用户在选之前做技术选择；授权退居为「可选加速」。 -->
    <template #empty-actions>
      <template v-if="isAndroid">
        <div class="empty-act-group">
          <button
            type="button"
            class="btn-primary btn-lg empty-act-btn"
            @click="onPickDirectory"
          >选择游戏目录</button>
          <span class="empty-act-hint">
            {{ game.allFilesAccess === true ? '选完自动用快速模式' : '未授权，选完用兼容模式' }}
          </span>
        </div>
        <!-- 已授权就不必再出现授权入口——那会读成「还得再做一步」 -->
        <div v-if="game.allFilesAccess !== true" class="empty-act-group">
          <button
            type="button"
            class="btn-outline empty-act-btn"
            @click="onGrantForFastMode"
          >获取所有文件访问</button>
          <span class="empty-act-hint">可选，授权后可用快速模式（不影响选目录）</span>
        </div>
      </template>
      <!-- Windows：无「所有文件访问」概念，保留单一原生选择器按钮（与改动前一致） -->
      <button
        v-else
        type="button"
        class="btn-primary btn-lg empty-act-btn"
        @click="onChangeMainDir"
      >更改主目录</button>
    </template>

    <template #appbar-actions>
      <button
        type="button"
        class="appbar-menu-btn"
        :aria-label="showMenu ? '关闭目录操作菜单' : '目录操作菜单'"
        aria-haspopup="true"
        :aria-expanded="showMenu"
        @mousedown.stop
        @click="showMenu = !showMenu"
      >
        <svg viewBox="0 0 24 24" aria-hidden="true">
          <path d="M12 5v.01M12 12v.01M12 19v.01" />
        </svg>
      </button>
      <Transition name="popup">
        <PopupMenu
          v-if="showMenu"
          :items="dirMenuItems"
          @close="showMenu = false"
        />
      </Transition>
    </template>
  </GameLibraryView>
</template>

<style scoped>
/* issues/15 S3：空态动作组——「按钮 + 一行代价说明」成对，组间距由空态容器的 gap 提供 */
.empty-act-group {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--space-1);
  width: 100%;
}
.empty-act-btn {
  min-height: 44px;
  width: 100%;
  max-width: 280px;
}
.empty-act-hint {
  font-size: var(--font-size-sm);
  color: var(--color-text-muted);
}

/* 48px 圆形图标按钮（原 MauiGameList 样式，保留为插槽内容） */
.appbar-menu-btn {
  width: 48px;
  height: 48px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: transparent;
  color: var(--color-text);
  border: none;
  border-radius: 9999px;
  cursor: pointer;
  transition: background-color var(--motion-fast);
}
.appbar-menu-btn:hover {
  background: var(--state-layer-hover);
}
.appbar-menu-btn:active {
  background: var(--state-layer-active);
}
.appbar-menu-btn:focus-visible {
  background: var(--state-layer-focus);
  outline: 2px solid var(--color-focus);
  outline-offset: -2px;
}
.appbar-menu-btn svg {
  width: 24px;
  height: 24px;
  fill: none;
  stroke: currentColor;
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
}
</style>
