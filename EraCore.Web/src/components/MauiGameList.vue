<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue';
import { useGameStore } from '../stores/game';
import { useUiStore } from '../stores/ui';
import { mauiGameLibrarySource } from '../lib/gameLibrary';
import { isMauiEnvironment, checkAllFilesAccess, requestAllFilesAccess } from '../lib/mauiBridge';
import GameLibraryView from './GameLibraryView.vue';
import PopupMenu, { type PopupMenuItem } from './PopupMenu.vue';

/**
 * MauiGameList — MAUI 游戏选择页（PickerShell）。
 *
 * 只是 GameLibraryView（与 Web 共用一份设计）的薄 wrapper——
 * 平台差异只剩右上角 ⋮ 菜单（更改目录 / 重新扫描 / 主题 / 所有文件访问）：
 * - 数据获取（扫描/加载）经 mauiGameLibrarySource（C# 桥接）
 * - 「更改目录」直接弹原生 FolderPicker（Windows）/ SAF（Android），
 *   与 Web（手动输入对话框）的区别即平台能力差异
 */
const game = useGameStore();
const ui = useUiStore();

/** 顶部 ⋮ 菜单展开状态。 */
const showMenu = ref(false);

/** issues/15：菜单项可见性——MAUI + SAF 模式（mainGameDir 为 content:// URI）。
 *  legacy 模式落地后 mainGameDir 变真实路径，item 自然消失。 */
const showAllFilesItem = computed(
  () => isMauiEnvironment() && (game.mainGameDir?.startsWith('content://') ?? false),
);

/** issues/15：从系统设置页返回 app 后复查授权状态（visibilitychange 触发一次）。 */
function onVisibilityRecheck(): void {
  if (!document.hidden) {
    document.removeEventListener('visibilitychange', onVisibilityRecheck);
    checkAllFilesAccess();
  }
}

/** issues/15：点击「申请所有文件访问权限」——跳系统设置页，返回时自动复查。 */
function onRequestAllFilesAccess(): void {
  document.addEventListener('visibilitychange', onVisibilityRecheck);
  requestAllFilesAccess();
}

onMounted(() => {
  // 初始状态查询——菜单项标签据此显示「已授权 / 申请」
  if (showAllFilesItem.value) checkAllFilesAccess();
});

onUnmounted(() => {
  document.removeEventListener('visibilitychange', onVisibilityRecheck);
});

/** 目录操作菜单项——扫描中禁用；点击后由组件自动关闭。 */
const dirMenuItems = computed<PopupMenuItem[]>(() => [
  {
    type: 'item',
    id: 'change-dir',
    label: isScanning.value ? '扫描中…' : '更改目录...',
    disabled: isScanning.value,
    onClick: onChangeMainDir,
  },
  {
    type: 'item',
    id: 'rescan',
    label: '重新扫描游戏',
    disabled: isScanning.value,
    onClick: onRescan,
  },
  // issues/15：Android「所有文件访问」——legacy 存储模式（issue 15）的授权入口。
  // 仅 SAF 模式显示；授权后标签同步，legacy accessor 接入时经此开关切换。
  ...(showAllFilesItem.value
    ? [
        {
          type: 'item' as const,
          id: 'all-files-access',
          label:
            game.allFilesAccess === true
              ? '所有文件访问：已授权'
              : '申请所有文件访问权限',
          onClick: onRequestAllFilesAccess,
        },
      ]
    : []),
  { type: 'separator' },
  {
    type: 'item',
    id: 'theme',
    icon: ui.theme === 'dark' ? 'sun' : 'moon',
    label: ui.theme === 'dark' ? '亮色主题' : '暗色主题',
    onClick: () => ui.toggleTheme(),
  },
]);

/** 当前是否扫描中——菜单项禁用依据。 */
const isScanning = computed(() => game.scanStatus === 'scanning');

/** 点击「更改目录」——经 source 直接弹原生选择器（Windows FolderPicker / Android SAF）。 */
function onChangeMainDir(): void {
  game.clearMauiError();
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
