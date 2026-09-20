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
 * - 「更改目录…」Android 走页内真实路径浏览器（**推荐轨：直读**），Windows 走原生 FolderPicker
 * - 「改用系统选择器…」（Android）SAF 兜底轨，仅当目标目录只能在 SAF 下访问时才需要
 * - 「所有文件访问权限」（Android，恒显示）直读轨的前置条件，右侧直显当前状态
 * - issues/15 S3：空态给双按钮，把两条轨的代价（拿权限更快 / 免授权兼容）在首次使用时就说明
 *
 * 设计取向（2026-09-11 用户裁定）：**不隐藏任何一条路，而是把代价写进标签**——
 * 优先级靠措辞与顺序体现，用户自己选；选定后由路径形态决定模式（设计稿 §1）。
 */
const game = useGameStore();
const ui = useUiStore();

/** GameLibraryView 实例引用——目录入口经其暴露方法打开页内浏览对话框。 */
const glvRef = ref<InstanceType<typeof GameLibraryView> | null>(null);

/** 顶部 ⋮ 菜单展开状态。 */
const showMenu = ref(false);

/** issues/15 S3：Android MAUI——目录模式与授权相关入口只在此平台出现
 *  （Windows 恒真实路径且无授权概念，加这些项只是噪音）。 */
const isAndroid = computed(() => isAndroidMauiEnvironment());

/**
 * issues/15 S3：空态「获取所有文件访问」的待办意图。
 *
 * 授权要跳系统设置页、返回后授权状态是**异步回流**的，没法同步串联。故置此标记 +
 * watch 授权状态：**授权成功才**自动打开目录浏览器，让「授权 → 选目录」连成一个动作
 * （否则用户点完按钮回来「什么都没发生」，还得再点一次——正是改造前的困惑点）。
 * 用户没授权就返回则不打开：根列表是空的，开了反而更困惑。
 */
const pendingBrowseAfterGrant = ref(false);

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

/** issues/15 S3：空态「获取所有文件访问」——授权成功后自动进入目录浏览。 */
function onGrantForBrowse(): void {
  pendingBrowseAfterGrant.value = true;
  onRequestAllFilesAccess();
}

/**
 * issues/15 S3：空态主按钮——已授权就直接选目录（再让用户「获取权限」是废话），
 * 未授权先拿权限、拿到后自动接着选目录（见 onGrantForBrowse）。
 */
function onPrimaryEmptyAction(): void {
  if (game.allFilesAccess === true) {
    openBrowseDialog();
    return;
  }
  onGrantForBrowse();
}

/** 授权状态回流后兑现待办意图（见 pendingBrowseAfterGrant）。 */
watch(
  () => game.allFilesAccess,
  (granted) => {
    if (!pendingBrowseAfterGrant.value) return;
    pendingBrowseAfterGrant.value = false;
    if (granted === true) openBrowseDialog();
  },
);

onMounted(() => {
  // issues/15 S3：Android 恒查一次授权状态——菜单项与空态文案都要据此渲染。
  // 不能再像 S2 那样只在 SAF 模式下查（legacy 模式下的状态会陈旧）。
  if (isAndroid.value) checkAllFilesAccess();
});

onUnmounted(() => {
  document.removeEventListener('visibilitychange', onVisibilityRecheck);
  pendingBrowseAfterGrant.value = false;
});

/** 目录操作菜单项——扫描中禁用；点击后由组件自动关闭。 */
const dirMenuItems = computed<PopupMenuItem[]>(() => [
  {
    type: 'item',
    id: 'change-dir',
    label: isScanning.value ? '扫描中…' : '更改目录...',
    // issues/15 S3：右侧小字把「这条路的代价」写在点之前（Windows 无此差异，不加 hint）
    ...(isAndroid.value ? { hint: '直读，更快' } : {}),
    disabled: isScanning.value,
    onClick: onChangeMainDir,
  },
  // issues/15 S3：SAF 兜底轨——目标目录只在 SAF 下可读（Android/data、云盘 provider）时的出口。
  // 与「更改目录…」并列而非隐藏：两条路的代价已写进右侧文案，用户能自行分辨。
  ...(isAndroid.value
    ? [
        {
          type: 'item' as const,
          id: 'pick-saf',
          label: '改用系统选择器...',
          hint: '兼容，无需授权',
          disabled: isScanning.value,
          onClick: onUseSystemPicker,
        },
      ]
    : []),
  // issues/15 S3：授权项**恒显示**。S2 时它只在 SAF 模式下出现，于是 legacy 模式下若授权被
  // 系统收回，就没有重新授权的入口了。右侧直显状态，让它读起来像「一个为了直读的开关」。
  ...(isAndroid.value
    ? [
        {
          type: 'item' as const,
          id: 'all-files-access',
          label: '所有文件访问权限',
          hint: game.allFilesAccess === true ? '已授权' : '未授权，点此申请',
          onClick: onRequestAllFilesAccess,
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
 * 「更改目录…」——Android 走页内**真实路径**浏览器（推荐轨：直读，能选出真实路径）；
 * Windows 保持原生 FolderPicker。未授权时对话框内直接给「授予」按钮，不再让用户去 ⋮ 里找。
 */
function onChangeMainDir(): void {
  game.clearMauiError();
  if (isAndroid.value) {
    openBrowseDialog();
    return;
  }
  void mauiGameLibrarySource.pickMainDir();
}

/** issues/15 S3：打开页内浏览器（ChangeDirDialog），并清掉待办意图——避免授权回流后再开一次。
 *  传 `browse: true` 让对话框**直接展开浏览区**（菜单项承诺「直读，更快」，不该停在输入框）。 */
function openBrowseDialog(): void {
  pendingBrowseAfterGrant.value = false;
  game.clearMauiError();
  game.clearLoadGameError();
  glvRef.value?.openChangeDirDialog({ browse: true });
}

/** issues/15 S3：「改用系统选择器…」——SAF 兜底轨，只产 content:// URI。 */
function onUseSystemPicker(): void {
  game.clearMauiError();
  pickSafDirectory();
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
  <GameLibraryView ref="glvRef" :source="mauiGameLibrarySource">
    <!-- issues/15 S3：Android 首次使用就把两条轨的代价说明白——顺序即优先级，推荐路径在前。
         这里不隐藏任何一条：用户看过代价后自行选，选定后由路径形态决定模式。 -->
    <template #empty-actions>
      <template v-if="isAndroid">
        <div class="empty-act-group">
          <button
            type="button"
            class="btn-primary btn-lg empty-act-btn"
            @click="onPrimaryEmptyAction"
          >{{ game.allFilesAccess === true ? '选择游戏目录' : '获取所有文件访问' }}</button>
          <span class="empty-act-hint">
            {{ game.allFilesAccess === true ? '直读模式，加载更快' : '授予后直接读取文件，加载更快' }}
          </span>
        </div>
        <div class="empty-act-group">
          <button
            type="button"
            class="btn-outline empty-act-btn"
            @click="onUseSystemPicker"
          >改用系统选择器</button>
          <span class="empty-act-hint">无需授权，兼容模式</span>
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
