<script setup lang="ts">
import { ref, computed, watch, onMounted, onUnmounted } from 'vue';
import { useGameStore, mapLoadGameErrorCode, formatMainGameDirForDisplay } from '../stores/game';
import { useConnectionStore } from '../stores/connection';
import { isAndroidMauiEnvironment, type GameLibrarySource } from '../lib/gameLibrary';
import ChangeDirDialog from './ChangeDirDialog.vue';
import ConfirmDialog from './ConfirmDialog.vue';
import GameRow from './GameRow.vue';
import { rescanIrCache } from '../lib/mauiBridge';
import StatusBanner from './StatusBanner.vue';

/**
 * GameLibraryView — MAUI 与 Web/HTTP **共用的游戏选择页**（单一设计源）。
 *
 * 页面体（hero / 路径行 / 游戏列表 / 空状态）两边渲染完全同一份布局；
 * 改目录统一走 {@link ChangeDirDialog}（手动输入 + 平台选择器：MAUI 原生 / Web 浏览），
 * **不再在页面体内联目录浏览**，因此 MAUI 与 Web 的主布局一致。
 *
 * 数据获取（扫描 / 加载）经 <see cref="GameLibrarySource"/> 抽象：
 * - maui：C# 桥接（scanGames → gamesScanned 事件回流 store），原生 FolderPicker/SAF
 * - http：C# server /game/scan、/game/dirs，手动输入 + 页面内浏览（都在对话框内）
 *
 * 顶部应用栏右侧内容由父组件经 `#appbar-actions` 插槽注入
 * （MAUI：⋮ 菜单 = 更改目录/重新扫描/主题；Web：连接状态按钮）。
 */
const props = defineProps<{
  /** 游戏库数据源（maui 桥接 或 http）。 */
  source: GameLibrarySource;
}>();

const game = useGameStore();
const conn = useConnectionStore();

/** 原生选择器可用（MAUI）——「更改目录」分派：MAUI 直接原生选择器，Web 打开手动输入对话框。 */
const isNative = computed(() => props.source.supportsNativePicker);

/** 主目录展示文案——优先扫库回执带回的可读路径（SAF 的 content URI 解成「内部存储 / …」），
 *  没有时回退格式化的 scanRootDir（最近扫描的目录）、再退 mainGameDir。 */
const mainDirDisplay = computed(() =>
  game.scanRootDirDisplay
  ?? formatMainGameDirForDisplay(game.scanRootDir ?? game.mainGameDir),
);

/**
 * 主目录**展示文案**按 `/` 切成片段，供模板用 `<wbr>` 标出**可折行点**。
 *
 * 路径没有空格，默认只能靠 `word-break: break-all` 在任意字符处断——加了模式徽章后可用宽度
 * 变窄，会在词中间断开（`/em` / `uera`）。切成片段后折行只发生在分隔符处，读起来是完整的段。
 *
 * 注意取的是 {@link mainDirDisplay}（SAF 的 content URI 已解成「内部存储 / games / eratoho」，
 * 只有解析不出 documentId 时才退回折叠常量）而非原始路径——用原始值会把整条 `content://` URI 铺出来。
 */
const mainDirSegments = computed<string[]>(() => {
  const dir = mainDirDisplay.value;
  if (!dir || dir === '(未设置)') return [];
  const parts: string[] = [];
  let start = 0;
  for (let i = 0; i < dir.length; i++) {
    if (dir[i] === '/' && i > start) {
      parts.push(dir.slice(start, i + 1));
      start = i + 1;
    }
  }
  parts.push(dir.slice(start));
  return parts.filter((s) => s.length > 0);
});

/**
 * issues/15 S3：是否展示目录模式徽章。
 *
 * 仅 Android MAUI——Windows 恒真实路径（徽章是无信息量的噪音），Web 没有「模式」概念。
 */
const showModeBadge = computed(() => props.source.kind === 'maui' && isAndroidMauiEnvironment());

/**
 * 当前主目录所属的轨——**由路径形态派生，不来自任何模式字段**（设计稿 §1）。
 *
 * `content://` = 兼容轨（SAF 文档访问）；真实路径 = 直读轨。
 * 用户需要「知道自己在哪条轨上」，因为这个差异决定了加载速度（issue 15 实测 −42%）。
 *
 * **必须读原始路径**：`game.scanRootDirDisplay` 是给渲染用的可读名（如「内部存储 / games」），
 * 不含 `content://` 前缀——拿它判模式会把 SAF 轨误判成快速轨。
 */
const dirMode = computed<'legacy' | 'saf' | null>(() => {
  const dir = game.scanRootDir ?? game.mainGameDir;
  if (!dir) return null;
  return dir.startsWith('content://') ? 'saf' : 'legacy';
});

/** 扫描中——展示 loading 占位。 */
const isScanning = computed(() => game.scanStatus === 'scanning');

/** 加载游戏中——禁用交互。 */
const isLoading = computed(() => game.reloadStatus === 'loading');

/** 旁观中——禁点游戏（与 GamePicker 语义一致）。 */
const loadBlocked = computed(() => isLoading.value || !conn.canMutateLifecycle);

/** 列表为空——scanStatus='idle' + 无游戏才算空状态。 */
const isEmpty = computed(
  () => game.scanStatus === 'idle' && game.scannedGames.length === 0,
);

// ---------- 错误条（合并 mauiError / loadGameError / 本地传输错误） ----------
/** 本地错误文案——scan / browse / load 传输失败时写入。 */
const localError = ref<string | null>(null);
const currentError = computed<string | null>(() => {
  if (localError.value) return localError.value;
  if (game.mauiError) return game.mauiError;
  if (game.loadGameError) {
    return `${mapLoadGameErrorCode(game.loadGameError.code)}：${game.loadGameError.message}`;
  }
  return null;
});
let errorBannerTimer: ReturnType<typeof setTimeout> | null = null;

function onDismissError(): void {
  localError.value = null;
  game.clearMauiError();
  game.clearLoadGameError();
  if (errorBannerTimer) {
    clearTimeout(errorBannerTimer);
    errorBannerTimer = null;
  }
}

/** 错误条 5 秒自动消失——watch currentError 变化时重启定时器。 */
function scheduleErrorBannerAutoDismiss(): void {
  if (errorBannerTimer) {
    clearTimeout(errorBannerTimer);
    errorBannerTimer = null;
  }
  if (currentError.value) {
    errorBannerTimer = setTimeout(onDismissError, 5000);
  }
}
watch(currentError, () => scheduleErrorBannerAutoDismiss(), { immediate: true });

// ---------- issues/15（方案 B）：模式降级告知 ----------
/**
 * 「所有文件访问」被系统收回时，主目录会自动退回落地的 SAF URI（快速模式 → 兼容模式）。
 * 用户什么都没做却变慢了，必须明说一句——用 info 条（不是错误：游戏照样能玩），
 * 与错误条同策略 5 秒自动消失，避免长期占位。
 */
let modeNoticeTimer: ReturnType<typeof setTimeout> | null = null;

function dismissModeNotice(): void {
  game.clearModeNotice();
  if (modeNoticeTimer) {
    clearTimeout(modeNoticeTimer);
    modeNoticeTimer = null;
  }
}

function scheduleModeNoticeAutoDismiss(): void {
  if (modeNoticeTimer) {
    clearTimeout(modeNoticeTimer);
    modeNoticeTimer = null;
  }
  if (game.modeNotice) {
    modeNoticeTimer = setTimeout(dismissModeNotice, 5000);
  }
}
watch(() => game.modeNotice, () => scheduleModeNoticeAutoDismiss(), { immediate: true });

// ---------- 「更改目录」入口（统一走 ChangeDirDialog） ----------
const dirDialogVisible = ref(false);
// issues/13 M5：重扫脚本缓存（路线 2 显式失效）——⋮ 菜单 → 确认 → 删 IR+sidecar，下次启动建库轮。
const rescanTarget = ref<{ name: string; fullPath: string } | null>(null);
function onRequestRescan(name: string, fullPath: string): void {
  rescanTarget.value = { name, fullPath };
}
function onConfirmRescan(): void {
  if (rescanTarget.value) {
    rescanIrCache(rescanTarget.value.fullPath);
    rescanTarget.value = null;
  }
}

/** 以指定路径为主目录并发起扫描——成功与否由 source 判断，失败写本地错误。 */
async function scanDir(dir: string): Promise<void> {
  const target = dir.trim();
  if (!target) return;
  localError.value = null;
  const ok = await props.source.scan(target);
  if (!ok) {
    localError.value = '无法连接服务器——请确认 C# server 正在运行（检查右侧连接设置）。';
  }
}

/** 打开共享的「更改目录」对话框（Web 经右上角菜单调用）。
 *
 *  原有一个 `{ browse: true }` 选项用以「打开即展开浏览区」，服务 MAUI 菜单入口的承诺；
 *  该入口自 issues/15 方案 B 起改走系统选择器，选项随之删除。 */
function openChangeDirDialog(): void {
  game.clearMauiError();
  game.clearLoadGameError();
  dirDialogVisible.value = true;
}

/** 「更改目录」统一入口——MAUI 直接弹原生选择器，Web 打开手动输入对话框。 */
function changeMainDir(): void {
  if (isNative.value) {
    game.clearMauiError();
    void props.source.pickMainDir();
  } else {
    openChangeDirDialog();
  }
}

/** 点击游戏列表项——经 source.pickGame 加载（MAUI hot-swap / HTTP /load-game）。 */
async function onPickGame(name: string, fullPath: string): Promise<void> {
  if (loadBlocked.value) return;
  localError.value = null;
  await props.source.pickGame(name, fullPath);
}

// ---------- hero 滚动效果（原型3：滚动后顶部出现紧凑标题 + 淡出 hero） ----------
const compactTitleScrollThreshold = 24;
const isScrolled = ref(false);
const heroOpacity = ref(1);
let heroIntroTimer: ReturnType<typeof setTimeout> | null = null;
const heroIntro = ref(true);

function onWindowScroll(): void {
  const scrollY = window.scrollY;
  isScrolled.value = scrollY > 0;
  if (scrollY > 0) heroIntro.value = false;
  heroOpacity.value = Math.max(0, 1 - scrollY / compactTitleScrollThreshold);
}

onMounted(() => {
  window.addEventListener('scroll', onWindowScroll, { passive: true });
  onWindowScroll();
  heroIntroTimer = setTimeout(() => {
    heroIntro.value = false;
  }, 540);
  // 启动自动扫描：MAUI 由 useAppInit 首扫（bridge 事件驱动），Web 由视图首扫（避免双扫）。
  if (props.source.kind === 'http') {
    const dir = game.mainGameDir;
    if (dir) void scanDir(dir);
  }
});

onUnmounted(() => {
  window.removeEventListener('scroll', onWindowScroll);
  if (heroIntroTimer) clearTimeout(heroIntroTimer);
  if (errorBannerTimer) clearTimeout(errorBannerTimer);
  if (modeNoticeTimer) clearTimeout(modeNoticeTimer);
});

/** 暴露给外层 wrapper（如 MauiGameList ⋮ 菜单）打开共享「更改目录」对话框。 */
defineExpose({ openChangeDirDialog });
</script>

<template>
  <div class="game-library-view" :class="{ 'is-scrolled': isScrolled }">
    <!-- 顶部应用栏：紧凑标题居中 + 右侧插槽（⋮ 菜单 / 连接设置） -->
    <header class="picker-appbar">
      <span class="compact-title" aria-hidden="true">选择游戏</span>
      <div class="appbar-actions">
        <slot name="appbar-actions" :is-scanning="isScanning" />
      </div>
    </header>

    <!-- hero 大标题（原型3）：功能标题，不显示产品名 -->
    <div
      class="hero-title"
      :class="{ 'hero-intro': heroIntro }"
      :style="{ opacity: heroOpacity }"
    >选择游戏</div>

    <!-- 路径行：当前目录展示（改目录经右上角菜单 → 对话框/原生选择器，两平台对齐） -->
    <div class="path-line">
      <span class="path-prefix">当前目录</span>
      <span class="path-value" :title="mainDirDisplay">
        <template v-if="mainDirSegments.length">
          <template v-for="(seg, i) in mainDirSegments" :key="i">{{ seg }}<wbr /></template>
        </template>
        <template v-else>未选择目录</template>
      </span>
      <!-- issues/15 S3：模式徽章——让用户看得见自己走的是哪条轨（快速 / 兼容）。
           文案只报状态；「更快」这类代价说明留给入口处（菜单 / 空态 / 对话框），
           避免同一屏出现「状态 + 卖点」混合体。 -->
      <span
        v-if="showModeBadge && dirMode"
        class="mode-badge"
        :class="dirMode === 'legacy' ? 'mode-legacy' : 'mode-saf'"
      >
        {{ dirMode === 'legacy' ? '快速模式' : '兼容模式' }}
      </span>
    </div>

    <div class="glv-body">
      <!-- 提示条区：错误条 + 模式降级告知（都是顶部可关闭，5s 自动消失） -->
      <div v-if="currentError" class="banner-slot">
        <StatusBanner kind="error" closable @close="onDismissError">
          {{ currentError }}
        </StatusBanner>
      </div>
      <!-- issues/15（方案 B）：模式降级告知——info 语义，不是错误（游戏照样能玩） -->
      <div v-if="game.modeNotice" class="banner-slot">
        <StatusBanner kind="info" closable @close="dismissModeNotice">
          {{ game.modeNotice }}
        </StatusBanner>
      </div>

      <!-- 扫描状态（轻量 spinner + 文案） -->
      <div v-if="isScanning" class="scanning">
        <span class="spinner" aria-hidden="true" />
        <span>正在扫描游戏列表…</span>
      </div>

      <!-- 空状态 -->
      <div v-else-if="isEmpty" class="empty-state">
        <template v-if="game.scanRootDirExists === false">
          <div class="empty-title">主目录不存在</div>
          <div class="empty-hint">请手动创建以下目录并把游戏放进去：</div>
        </template>
        <template v-else>
          <div class="empty-title">未找到游戏</div>
          <div class="empty-hint">请把游戏放到以下目录：</div>
        </template>
        <div class="empty-path" :title="mainDirDisplay">{{ mainDirDisplay }}</div>
        <div class="empty-hint">游戏目录需要包含 csv/ 和 erb/ 两个子目录</div>
        <!-- issues/15 S3：平台可注入空态动作——MAUI 给「取权限用直读 / 直接用系统选择器」
             双按钮（把两条轨的代价差异明说），Web 走兜底单按钮，行为不变 -->
        <slot name="empty-actions">
          <button
            type="button"
            class="btn-primary btn-lg empty-primary-btn"
            @click="changeMainDir"
          >更改主目录</button>
        </slot>
      </div>

      <!-- 游戏列表（文件管理器式整行） -->
      <div v-else class="game-list-wrapper">
        <ul class="game-list">
          <GameRow
            v-for="g in game.scannedGames"
            :key="g.fullPath"
            :name="g.name"
            :last-played="g.name === game.lastPlayedGame"
            :ir-cached="g.irCached"
            :ir-cache-bytes="g.irCacheBytes"
            :ir-cache-built-utc="g.irCacheBuiltUtc"
            @click="onPickGame(g.name, g.fullPath)"
            @rescan="onRequestRescan(g.name, g.fullPath)"
          />
        </ul>
      </div>
    </div>

    <!-- 共享「更改目录」对话框（Web：手动输入 + 逐层浏览；MAUI 走原生选择器，不打开它） -->
    <ChangeDirDialog
      v-model:visible="dirDialogVisible"
      :source="source"
    />

    <!-- issues/13 M5：重扫脚本缓存确认——删 IR+sidecar 不可逆（下次启动付一次建库轮 ~30s），需用户确认 -->
    <ConfirmDialog
      :visible="rescanTarget !== null"
      title="重扫脚本缓存"
      :message="rescanTarget ? `删除 ${rescanTarget.name} 的脚本缓存并重建？下次进入该游戏时将重新全量扫描（约需 30 秒）。若已更换 mod，这是让新脚本生效的方式。` : ''"
      confirm-label="删除并重扫"
      @confirm="onConfirmRescan"
      @cancel="rescanTarget = null"
      @update:visible="rescanTarget = null"
    />
  </div>
</template>

<style scoped>
.game-library-view {
  width: 100%;
  /* 双声明：dvh 不可用（旧 WebView）时落到 vh */
  min-height: 100vh;
  min-height: 100dvh;
  display: flex;
  flex-direction: column;
  background: var(--color-bg);
  color: var(--color-text);
  font-family: var(--font-ui);
  font-size: var(--font-size-base);
}

/* 顶部应用栏（原型3）——紧凑标题居中，右侧插槽 */
.picker-appbar {
  position: sticky;
  top: 0;
  z-index: 40;
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: var(--appbar-height);
  padding: var(--space-2) var(--space-4) 0;
  background: var(--color-bg);
  transition: background-color var(--motion-mid) ease;
}
.appbar-actions {
  position: absolute;
  right: var(--space-3);
  top: var(--space-2);
  display: flex;
  align-items: center;
  gap: var(--space-1);
}
.compact-title {
  position: absolute;
  left: 50%;
  top: 50%;
  transform: translate(-50%, calc(-50% + 8px));
  color: var(--color-text);
  font-size: var(--font-size-lg);
  line-height: 24px;
  opacity: 0;
  pointer-events: none;
  transition: opacity var(--motion-mid) ease, transform var(--motion-mid) ease;
}
.game-library-view.is-scrolled .compact-title {
  opacity: 1;
  transform: translate(-50%, -50%);
}

/* hero 大标题（原型3） */
.hero-title {
  padding: var(--space-3) var(--space-5);
  font-size: 32px;
  font-weight: 400;
  line-height: 40px;
  color: var(--color-text);
  transition: opacity var(--motion-slow) cubic-bezier(0.2, 0, 0, 1);
}
.hero-title.hero-intro {
  animation: picker-hero-in 0.52s ease both;
}

/* 路径行（原型3）：prefix + mono value + 更改主目录/浏览目录 */
.path-line {
  position: sticky;
  top: var(--appbar-height);
  z-index: 25;
  width: 100%;
  margin: 0 0 18px;
  padding: 0 var(--space-5) var(--space-3);
  background: var(--color-bg);
  display: flex;
  align-items: baseline;
  gap: 10px;
  min-width: 0;
}
.path-prefix {
  color: var(--color-text-muted);
  font-size: var(--font-size-md);
  line-height: 20px;
  opacity: 0.6;
  flex-shrink: 0;
}
.path-value {
  flex: 1;
  min-width: 0;
  /* 折行点由模板里的 <wbr>（路径分隔符处）提供；break-word 只作兜底——
     单段过长时仍能断，但不会像 break-all 那样在任意字符处断开 */
  word-break: normal;
  overflow-wrap: break-word;
  font-family: var(--font-mono);
  font-size: var(--font-size-base);
  line-height: 22px;
  color: var(--color-text);
  opacity: 0.6;
}

/* issues/15 S3：目录模式提示（仅 Android MAUI）。
   与同页 GameRow 的 IR 徽章同构——弱化色文字、无容器，提示仅辅助（不喧宾夺主）：
   曾经是「满饱和度文字 + 16% 填充 + 40% 描边」，在同一屏里是唯一带边框的彩色块，读起来像告警。
   去掉填充后不再需要 color-mix 与旧 WebView 兜底双声明。
   align-self: baseline 对齐 mono 路径的文字基线；font-weight 500 对齐设计系统 label 字重。 */
.mode-badge {
  flex-shrink: 0;
  align-self: baseline;
  font-size: var(--font-size-sm);
  font-weight: 500;
  line-height: 1.4;
  white-space: nowrap;
  opacity: 0.85;
}
.mode-legacy {
  color: var(--color-success);
}
.mode-saf {
  color: var(--color-warning);
}

.glv-body {
  flex: 1;
  overflow: visible;
  display: flex;
  flex-direction: column;
  padding: 0 0 var(--space-5);
  padding-bottom: env(safe-area-inset-bottom);
}
.banner-slot {
  padding-bottom: var(--space-2);
}
.scanning {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--space-2);
  padding: var(--space-4);
  text-align: center;
  color: var(--color-text-muted);
  font-size: var(--font-size-md);
}
.spinner {
  width: 16px;
  height: 16px;
  border: 2px solid var(--color-border);
  border-top-color: var(--color-indicator);
  border-radius: 50%;
  animation: glv-spin var(--motion-spin) linear infinite;
  flex-shrink: 0;
}
@keyframes glv-spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

/* 空状态（spec §5.4：居中紧凑，主按钮唯一） */
.empty-state {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--space-2);
  padding: var(--space-5) var(--space-4);
  text-align: center;
  max-width: var(--picker-list-max-width);
  margin: 0 auto;
  width: 100%;
}
.empty-title {
  font-size: var(--font-size-title);
  font-weight: 600;
  color: var(--color-text);
}
.empty-hint {
  font-size: var(--font-size-sm);
  color: var(--color-text-muted);
}
.empty-path {
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
  color: var(--color-success);
  word-break: break-all;
  max-width: 100%;
  padding: var(--space-1) var(--space-2);
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-control);
}
.empty-primary-btn {
  margin-top: var(--space-3);
}

/* 游戏列表（spec §5.3：文件管理器式整行，无卡片） */
.game-list-wrapper {
  flex: 1;
  display: flex;
  flex-direction: column;
  padding-inline: var(--space-5);
}
.game-list {
  list-style: none;
  margin: 0;
  padding: 0;
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
  max-width: none;
  width: 100%;
  margin-left: auto;
  margin-right: auto;
}

@keyframes picker-hero-in {
  from { opacity: 0; }
  to { opacity: 1; }
}
</style>
