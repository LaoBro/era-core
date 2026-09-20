<script setup lang="ts">
import { ref, computed } from 'vue';
import { useGameStore } from '../stores/game';
import { useUiStore } from '../stores/ui';
import { httpGameLibrarySource } from '../lib/gameLibrary';
import GameLibraryView from './GameLibraryView.vue';
import PopupMenu, { type PopupMenuItem } from './PopupMenu.vue';

/**
 * WebGameList — Web/HTTP 模式的游戏选择页。
 *
 * 只是 GameLibraryView（与 MAUI 共用一份设计）的薄 wrapper——
 * 右上角 ⋮ 菜单与 MAUI 完全一致（更改目录 / 重新扫描 / 主题）：
 * - 数据获取（扫描/目录浏览/加载）经 httpGameLibrarySource（C# server /game/scan、/game/dirs）
 * - 「更改目录」打开手动输入对话框（ChangeDirDialog，GameLibraryView 内部挂载），
 *   与 MAUI 直接弹原生选择器的区别即平台能力差异
 * - 前端与 server 绑定，无连接配置 UI；菜单结构即 MAUI 结构
 */
const game = useGameStore();
const ui = useUiStore();

/** GameLibraryView 实例引用——菜单「更改目录」经其暴露方法打开手动输入对话框。 */
const glvRef = ref<InstanceType<typeof GameLibraryView> | null>(null);

/** 顶部 ⋮ 菜单展开状态。 */
const showMenu = ref(false);

/** 当前是否扫描中——菜单项禁用依据。 */
const isScanning = computed(() => game.scanStatus === 'scanning');

/** 右上角菜单项——与 MAUI 菜单完全一致（更改目录 / 重新扫描 / 主题）。 */
const menuItems = computed<PopupMenuItem[]>(() => [
  {
    type: 'item',
    id: 'change-dir',
    label: isScanning.value ? '扫描中…' : '更改目录...',
    disabled: isScanning.value,
    onClick: () => glvRef.value?.openChangeDirDialog(),
  },
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
  // ADR-0030：游戏选择页设置入口——全局设置（走 /settings 端点，不进游戏也能改）
  {
    type: 'item',
    id: 'settings',
    icon: 'settings',
    label: '设置',
    onClick: () => ui.switchView('settings'),
  },
]);

/** 重新扫描——以当前主目录为起点重扫（未选目录时退化为更改目录对话框）。 */
function onRescan(): void {
  const dir = game.scanRootDir ?? game.mainGameDir;
  if (!dir) {
    glvRef.value?.openChangeDirDialog();
    return;
  }
  game.setMainGameDir(dir);
  void httpGameLibrarySource.scan(dir);
}
</script>

<template>
  <GameLibraryView ref="glvRef" :source="httpGameLibrarySource">
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
          :items="menuItems"
          @close="showMenu = false"
        />
      </Transition>
    </template>
  </GameLibraryView>
</template>

<style scoped>
/* 48px 圆形图标按钮（与 MauiGameList 一致——右上角菜单入口视觉对齐） */
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