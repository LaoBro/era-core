<script setup lang="ts">
/**
 * ChangeDirDialog.vue — 「更改目录」对话框（手动输入路径 + 页内目录浏览）。
 *
 * 两个平台共用：
 * - **Web/HTTP**：手动输入 + 页内浏览（`source.listDirs` 走 server `/game/dirs`）。
 * - **MAUI**：右上角 ⋮ 菜单「更改目录」直接弹原生选择器（Windows FolderPicker / Android SAF），
 *   本对话框作为**并列第二条路**——issues/15 S2（C2）的「浏览存储目录…」入口打开它，
 *   逐层选出一个**真实路径**目录，从而切到 legacy 直读模式（SAF picker 只产 content:// URI，
 *   拿不到真实路径）。MAUI 侧浏览走 `listDirectories` / `storageRoots` 桥接消息。
 *
 * 内容：手动路径输入（未展开浏览时）+ 面包屑式页内浏览（`source.listDirs` 逐层列举，
 * 「上级/前往/存储」收编为可点面包屑段）；浏览中确认（「扫描此目录」）→ 直接
 * `source.scan(browsePath)` 并关闭（手动模式下仍是填框 + 「扫描」），传输失败时保留框体。
 *
 * 视觉与交互遵循 ConfirmDialog.vue 的 Alert 式对话框约定（半透明遮罩 + 表面容器、
 * 点击遮罩 / Escape 关闭），按钮复用全局 .btn-primary / .btn-outline。
 */
import { ref, computed, watch, nextTick, onMounted, onUnmounted } from 'vue';
import { useGameStore } from '../stores/game';
import {
  isAndroidMauiEnvironment,
  type GameLibrarySource,
  type DirListResult,
  type StorageRoot,
} from '../lib/gameLibrary';
import { checkAllFilesAccess, requestAllFilesAccess } from '../lib/mauiBridge';

const props = defineProps<{
  /** 是否显示。 */
  visible: boolean;
  /** 游戏库数据源（maui 桥接 或 http）。 */
  source: GameLibrarySource;
  /**
   * issues/15 S3：打开后是否**直接展开浏览区**。
   *
   * ⋮ 菜单项写着「直读，更快」，点进来却停在输入框、还要再点一次「浏览…」——承诺与落点不一致。
   * 菜单入口传 true 让「点进来 = 看到目录」；Web 入口不传，保持手动输入为主的原行为。
   */
  autoBrowse?: boolean;
}>();

const emit = defineEmits<{
  (e: 'update:visible', v: boolean): void;
}>();

const game = useGameStore();

/**
 * Android（MAUI 桥接）下手动输入整个隐藏——真机软键盘输入长路径纯属折磨，
 * 且 Android 目录起点恒有存储根兜底，手动输入没有不可替代的场景（用户 2026-09-11 确认）。
 * Web/HTTP（桌面浏览器）保留手动输入：粘贴路径是正经用法。
 */
const isAndroidMaui = computed(() => isAndroidMauiEnvironment());

/** 手动路径输入——打开时预填当前主目录（scanRootDir 优先，fallback mainGameDir）。 */
const inputPath = ref<string>('');

/** 本地传输错误文案——scan / browse 失败时写入，显示在输入框下方。 */
const localError = ref<string | null>(null);

/** 对话框打开前获得焦点的元素——关闭时归还。 */
let previouslyFocused: HTMLElement | null = null;

/** 打开时初始化输入路径 + 记录焦点；关闭时归还焦点。 */
watch(
  () => props.visible,
  async (v) => {
    if (v) {
      inputPath.value = game.scanRootDir ?? game.mainGameDir ?? '';
      browsing.value = false;
      browseLoading.value = false;
      inRootsView.value = false;
      roots.value = null;
      needsGrant.value = false;
      awaitingGrant.value = false;
      localError.value = null;
      previouslyFocused = document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null;
      await nextTick();
      // UX 改版：浏览展开时不聚焦手动输入框——真机上会拉起软键盘把目录列表顶出视口。
      if (!props.autoBrowse) inputRef.value?.focus();
      // issues/15 S3：菜单入口承诺「点到即见目录」——打开即展开浏览区。
      // Android 下无条件展开（手动输入已隐藏，浏览是唯一模式）；enterBrowse
      // 自带起点分派：当前目录是真实路径就从它起，否则列存储根。
      if (props.autoBrowse || isAndroidMaui.value) {
        browsing.value = true;
        await enterBrowse(inputPath.value.trim());
      }
    } else if (previouslyFocused && previouslyFocused.isConnected) {
      previouslyFocused.focus();
      previouslyFocused = null;
    }
    // 关闭即放弃「待授权重试」意图——否则它会在下一次授权状态变化时触发一次不可见的列举
    if (!v) awaitingGrant.value = false;
  },
);

function close(): void {
  emit('update:visible', false);
}

/** 确认「扫描」——以输入路径为主目录（统一走 source.scan，传输失败保留框体）。 */
async function onConfirm(): Promise<void> {
  const target = inputPath.value.trim();
  if (!target) return;
  localError.value = null;
  game.clearMauiError();
  game.clearLoadGameError();
  const ok = await props.source.scan(target);
  if (!ok) {
    localError.value = '扫描失败——请确认 C# server 正在运行（检查连接设置）。';
    return;
  }
  close();
}

// ---------- 页面内目录浏览（面包屑导航 + 单步「扫描此目录」） ----------
const browsing = ref(false);
const browsePath = ref<string>('');
const browseDirs = ref<string[]>([]);
/** 正在读目录（桥接往返 / 慢卷枚举）——给浏览区块一个 loading 态。 */
const browseLoading = ref(false);
/** 存储根清单；null = 尚未取到或取失败（此时不显示根列表）。 */
const roots = ref<StorageRoot[] | null>(null);
/** 当前是否显示的是「存储根」列表（区别于普通子目录列表）。 */
const inRootsView = ref(false);
/**
 * issues/15 S3：根列表为空（= 未授权直读）——改为展示**内联授权 CTA**。
 *
 * 改造前这里只写一句「请先在 ⋮ 菜单中申请所有文件访问权限」——那是死循环：
 * ⋮ 菜单正被本对话框盖着，用户点不到。
 */
const needsGrant = ref(false);
/** 待授权后重试列举的意图——授权结果经桥接异步回流，故需标记 + watch 兑现。 */
const awaitingGrant = ref(false);

/** 数据源是否提供挂载点清单——MAUI 有（问 C#），Web 没有（可直接从任意路径起浏览）。 */
const canBrowseRoots = computed(() => typeof props.source.storageRoots === 'function');

/** 本平台是否存在「所有文件访问」权限这一说——决定根列表为空时给授权 CTA 还是纯错误文案。 */
const canGrant = computed(() => canBrowseRoots.value && isAndroidMauiEnvironment());

/**
 * 是否为可直接浏览的**真实路径**。
 *
 * `content://`（SAF）与空串都不是——此时真实路径浏览器没有可用起点，退到存储根清单。
 * 判定宽松（只要有根形态即可），真正能否列举由 `source.listDirs` 说了算。
 */
function isRealPath(p: string): boolean {
  const t = p.trim();
  if (!t || t.startsWith('content://')) return false;
  return t.startsWith('/') || /^[a-zA-Z]:[\\/]/.test(t);
}

/** 进入目录浏览——以当前输入路径为起点加载子目录（收起走面包屑行的 ⌨ 按钮）。 */
async function toggleBrowse(): Promise<void> {
  browsing.value = true;
  await enterBrowse(inputPath.value.trim());
}

/**
 * 进入目录浏览指定路径。
 *
 * issues/15 S2：路径不是真实路径（空 / `content://`）且数据源能给出挂载点时，
 * 改为展示**存储根清单**——这是 Android 从 SAF 切到 legacy 时的常态起点
 * （当前主目录本身就是 `content://`）。
 */
async function enterBrowse(dir: string): Promise<void> {
  const target = dir.trim();
  if (!isRealPath(target) && canBrowseRoots.value) {
    await showRoots();
    return;
  }
  // 面包屑依赖 roots 折叠存储前缀（💾 内部存储）——走真实路径时也要拉一次。
  // 幂等且便宜（单次桥接往返）；未授权时返回空清单，面包屑退化为逐段展开，无害。
  if (canBrowseRoots.value && roots.value === null) {
    roots.value = (await props.source.storageRoots?.()) ?? null;
  }

  browsePath.value = target;
  browseLoading.value = true;
  const result: DirListResult | null = await props.source.listDirs(target);
  browseLoading.value = false;
  if (result === null) {
    browseDirs.value = [];
    inRootsView.value = false;
    needsGrant.value = false;
    localError.value = `无法访问目录：${target || '(空)'}`;
    return;
  }
  browsePath.value = result.currentPath;
  browseDirs.value = result.dirs;
  inRootsView.value = false;
  localError.value = null;
}

/**
 * 读取并展示存储根清单（MAUI：C# 的 `StorageManager` / `DriveInfo`）。
 *
 * 空清单有两种含义，文案必须分开（issues/15 S3）：
 * - **Android 未授权**——C# 侧 `CollectStorageRoots` 的门直接返空（scoped storage 会拒绝对
 *   `/storage/emulated/0` 的直接路径访问），给可点的授权入口；
 * - 其它失败（该平台无授权概念 / 真读不到）——退化为手动输入的提示。
 */
async function showRoots(): Promise<void> {
  browseLoading.value = true;
  const list = (await props.source.storageRoots?.()) ?? null;
  browseLoading.value = false;
  if (!list || list.length === 0) {
    roots.value = null;
    inRootsView.value = false;
    browsePath.value = '';
    browseDirs.value = [];
    needsGrant.value = canGrant.value;
    localError.value = canGrant.value
      ? null
      : '无法读取存储位置——请在上方直接输入目录路径。';
    return;
  }
  roots.value = list;
  inRootsView.value = true;
  needsGrant.value = false;
  browsePath.value = '';
  browseDirs.value = [];
  localError.value = null;
}

/** issues/15 S3：内联授权入口——跳系统设置页，返回后复查。 */
function onGrantClick(): void {
  awaitingGrant.value = true;
  document.addEventListener('visibilitychange', onGrantVisibilityRecheck);
  requestAllFilesAccess();
}

/** 从系统设置页返回——复查授权（结果经 `watch(game.allFilesAccess)` 异步回流）。 */
function onGrantVisibilityRecheck(): void {
  if (document.hidden) return;
  document.removeEventListener('visibilitychange', onGrantVisibilityRecheck);
  checkAllFilesAccess();
}

/**
 * issues/15 S3：授权结果回流后兑现「待授权」意图——成功则立刻重试列举，
 * 让「授权 → 看到存储位置」连成一个动作；未授权则保留 CTA（不自作主张重试）。
 */
watch(
  () => game.allFilesAccess,
  (granted) => {
    if (!awaitingGrant.value) return;
    awaitingGrant.value = false;
    if (granted === true) void showRoots();
  },
);

/** 点击存储根——进入该卷浏览。 */
async function onPickRoot(path: string): Promise<void> {
  await enterBrowse(path);
}

/** 点击子目录——进入该目录继续浏览。 */
async function onPickDir(name: string): Promise<void> {
  const base = browsePath.value.replace(/[\\/]+$/, '');
  const sep = browsePath.value.includes('\\') ? '\\' : '/';
  await enterBrowse(name ? `${base}${sep}${name}` : base);
}

/**
 * 面包屑段（UX 改版）：由 browsePath 切分而来，取代「上级 / 路径输入框 / 前往 / 存储」四件套。
 *
 * - 有存储根清单时，最前面补一个**卷选择段**（「此电脑」/「存储」）——点它回存储根列表
 *   换卷。旧设备 / 平板 / 模拟器常有多卷（内部存储 + SD 卡、C: + D:），没有它进了第一个
 *   卷就再也切不出去（「存储」按钮被收编时漏掉的入口，在此补回）；
 * - 匹配到存储根时，根前缀折叠为**一个**可点段（显示 💾 卷名）——Android 上免看
 *   `/storage/emulated` 这类无人关心的中段；
 * - 否则按分隔符逐段展开（Windows 先给 `D:\` 盘符段，Unix 先给 `/` 根段）；
 * - 末段是当前位置，只读加粗不可点；点其余任意段 = 直接跳回该层（「上级」是其特例）。
 */
const crumbs = computed<{ label: string; path: string; isRoots?: boolean }[]>(() => {
  const p = browsePath.value.trim();
  if (!p || inRootsView.value) return [];
  const sep = p.includes('\\') ? '\\' : '/';
  const norm = p.replace(/[\\/]+$/, '');
  const out: { label: string; path: string; isRoots?: boolean }[] = [];
  // 卷选择段只在真有卷清单时出现——Web/HTTP 的 storageRoots 恒返 null，不该有换卷入口。
  if (roots.value && roots.value.length > 0) {
    out.push({ label: isAndroidMaui.value ? '存储' : '此电脑', path: '', isRoots: true });
  }
  let consumed = '';
  const root = roots.value?.find((r) => {
    const rp = r.path.replace(/[\\/]+$/, '');
    return rp !== '' && norm.toLowerCase().startsWith(rp.toLowerCase());
  });
  if (root) {
    consumed = root.path.replace(/[\\/]+$/, '');
    out.push({ label: root.label, path: consumed });
  } else if (sep === '\\') {
    const m = norm.match(/^[a-zA-Z]:/);
    consumed = m ? m[0] : norm.split('\\')[0];
    out.push({ label: `${consumed}\\`, path: `${consumed}\\` });
  } else {
    out.push({ label: '/', path: '/' });
    consumed = '';
  }
  const rest = norm.slice(consumed.length).split(/[\\/]/).filter(Boolean);
  let acc = consumed;
  for (const seg of rest) {
    acc = `${acc}${acc.endsWith(sep) ? '' : sep}${seg}`;
    out.push({ label: seg, path: acc });
  }
  return out;
});

/** 从浏览模式回到手动输入——把当前浏览位置填进输入框继续编辑（回车仍等于扫描）。 */
function collapseToManual(): void {
  if (browsePath.value.trim()) inputPath.value = browsePath.value;
  browsing.value = false;
}

/** 浏览中的单步确认：以当前浏览目录直接扫描并关闭（取代「使用此目录」+「扫描」两步）。 */
async function scanFromBrowse(): Promise<void> {
  const target = browsePath.value.trim();
  if (!target) return;
  inputPath.value = target;
  await onConfirm();
}

/** Escape（桌面键盘等价于 Android 返回键）——关闭对话框。 */
function onDocKeydown(e: KeyboardEvent): void {
  if (e.key === 'Escape' && props.visible) close();
}

const inputRef = ref<HTMLInputElement | null>(null);
onMounted(() => document.addEventListener('keydown', onDocKeydown));
onUnmounted(() => {
  document.removeEventListener('keydown', onDocKeydown);
  document.removeEventListener('visibilitychange', onGrantVisibilityRecheck);
});
</script>

<template>
  <Transition name="dialog">
    <div
      v-if="visible"
      class="cd-overlay"
      @click.self="close"
    >
      <div
        class="cd-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="更改目录"
      >
        <h2 class="cd-title">更改目录</h2>
        <p class="cd-hint">
          <template v-if="browsing">选择包含游戏的目录后点「扫描此目录」；真实路径目录走直读，更快。</template>
          <template v-else>
            输入游戏主目录（可包含多个游戏），或点「浏览…」逐层选择。
            <template v-if="canBrowseRoots">选择真实路径目录（如 /storage/emulated/0/…）可启用直读模式，加载更快。</template>
          </template>
        </p>

        <!-- 手动路径输入（浏览展开时隐藏——路径信息由面包屑唯一呈现，消除三处重复；
             Android 下彻底隐藏——软键盘输长路径是折磨，浏览是唯一模式） -->
        <div v-if="!browsing && !isAndroidMaui" class="cd-path-row">
          <input
            ref="inputRef"
            v-model="inputPath"
            class="cd-dir-input"
            type="text"
            placeholder="输入游戏主目录，例如 D:\games"
            spellcheck="false"
            @keyup.enter="onConfirm"
          />
          <button
            type="button"
            class="btn-outline cd-help-btn"
            @click="toggleBrowse"
          >浏览…</button>
        </div>

        <p v-if="localError" class="cd-error">{{ localError }}</p>

        <!-- 页面内目录浏览（面包屑导航；MAUI 走 listDirectories / storageRoots 桥接） -->
        <div v-if="browsing" class="cd-browser">
          <!--
            面包屑（UX 改版）：上级 / 前往 / 存储 三按钮 + 路径行的合体替代。
            点任意父段跳回该层；点 ⌨ 回手动输入。存储根视图与授权 CTA 不渲染面包屑。
          -->
          <nav
            v-if="crumbs.length"
            class="cd-crumbs"
            aria-label="目录层级"
          >
            <template v-for="(c, i) in crumbs" :key="c.path">
              <span
                v-if="i"
                class="cd-crumb-sep"
                aria-hidden="true"
              >›</span>
              <button
                v-if="i < crumbs.length - 1"
                type="button"
                class="cd-crumb"
                :title="c.isRoots ? '返回存储位置选择' : c.path"
                @click="c.isRoots ? showRoots() : enterBrowse(c.path)"
              >{{ c.label }}</button>
              <span
                v-else
                class="cd-crumb cd-crumb-current"
                :title="c.path"
                aria-current="location"
              >{{ c.label }}</span>
            </template>
            <button
              v-if="!isAndroidMaui"
              type="button"
              class="cd-crumb-edit"
              title="手动输入路径"
              aria-label="手动输入路径"
              @click="collapseToManual"
            >⌨</button>
          </nav>

          <div v-if="browseLoading" class="cd-browser-loading">
            <span class="cd-spinner" aria-hidden="true" />
            <span>正在读取目录…</span>
          </div>

          <!-- issues/15 S3：未授权直读 → 内联授权入口（不再是「请去被本对话框盖住的 ⋮ 菜单里申请」） -->
          <div v-else-if="needsGrant" class="cd-grant">
            <p class="cd-grant-text">需要「所有文件访问」权限才能浏览存储位置。</p>
            <button type="button" class="btn-primary" @click="onGrantClick">
              授予所有文件访问
            </button>
            <p class="cd-grant-hint">
              不想授权？关闭本对话框，用菜单里的「改用系统选择器...」——兼容模式，无需授权。
            </p>
          </div>

          <div v-else class="cd-browser-dirs">
            <!-- 存储根清单（issues/15 S2）：挂载点由系统给，作为浏览起点 -->
            <ul v-if="inRootsView && roots?.length" class="cd-dir-list">
              <li v-for="r in roots" :key="r.path">
                <button type="button" class="cd-dir-row" @click="onPickRoot(r.path)">
                  <span class="cd-dir-icon" aria-hidden="true">💾</span>
                  <span class="cd-dir-name">
                    {{ r.label }}
                    <span class="cd-dir-sub">{{ r.path }}</span>
                  </span>
                  <span class="cd-dir-arrow" aria-hidden="true">›</span>
                </button>
              </li>
            </ul>
            <ul v-else-if="browseDirs.length" class="cd-dir-list">
              <li v-for="d in browseDirs" :key="d">
                <button type="button" class="cd-dir-row" @click="onPickDir(d)">
                  <span class="cd-dir-icon" aria-hidden="true">📁</span>
                  <span class="cd-dir-name">{{ d }}</span>
                  <span class="cd-dir-arrow" aria-hidden="true">›</span>
                </button>
              </li>
            </ul>
            <div v-else class="cd-browser-empty">
              {{ inRootsView ? '没有可用的存储位置' : '此目录下没有子目录' }}
            </div>
          </div>

          <!-- 浏览模式的唯一确认：以当前浏览目录直接扫描（取消也收进这一行） -->
          <div class="cd-browser-footer">
            <button
              type="button"
              class="btn-outline"
              @click="close"
            >取消</button>
            <button
              type="button"
              class="btn-primary"
              :disabled="!browsePath.trim()"
              @click="scanFromBrowse"
            >扫描此目录</button>
          </div>
        </div>

        <div
          v-if="!browsing && !isAndroidMaui"
          class="cd-actions"
        >
          <button type="button" class="btn-outline cd-btn" @click="close">取消</button>
          <button
            type="button"
            class="btn-primary cd-btn"
            :disabled="!inputPath.trim()"
            @click="onConfirm"
          >扫描</button>
        </div>
      </div>
    </div>
  </Transition>
</template>

<style scoped>
.cd-overlay {
  position: fixed;
  inset: 0;
  background: var(--color-overlay);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: var(--space-4);
}
.cd-dialog {
  background: var(--color-surface);
  border: none;
  border-radius: var(--radius-surface);
  box-shadow: var(--elevation-menu);
  padding: var(--space-5);
  max-width: 460px;
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
}
.cd-title {
  margin: 0;
  font-size: var(--font-size-lg);
  font-weight: 600;
  color: var(--color-text);
}
.cd-hint {
  margin: 0;
  font-size: var(--font-size-sm);
  color: var(--color-text-muted);
}
.cd-path-row {
  display: flex;
  gap: var(--space-2);
  width: 100%;
}
.cd-dir-input {
  flex: 1;
  min-width: 0;
  background: var(--color-bg);
  color: var(--color-text);
  border: 1px solid var(--color-border);
  padding: 6px var(--space-2);
  border-radius: var(--radius-control);
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
}
.cd-dir-input:disabled {
  opacity: 0.6;
}
.cd-help-btn {
  flex-shrink: 0;
}
.cd-error {
  margin: 0;
  font-size: var(--font-size-sm);
  color: var(--color-danger);
}
.cd-browser {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
}
/* 面包屑（UX 改版）：可折行解决长路径截断；每段命中区 ≥40px 高，与目录行一致 */
.cd-crumbs {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0;
  background: var(--color-bg);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-control);
  padding: 0 var(--space-2);
}
.cd-crumb {
  /* inline-flex + center：span 形态的「当前段」没有 button 的 UA 垂直居中，文字会在
     40px 命中区里顶上去（真机实测「往上飘」）——统一用 flex 居中修掉。 */
  display: inline-flex;
  align-items: center;
  min-height: 40px;
  padding: 0 var(--space-1);
  background: transparent;
  border: none;
  color: var(--color-indicator);
  cursor: pointer;
  font-size: var(--font-size-sm);
  font-family: var(--font-ui);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 100%;
}
.cd-crumb:hover {
  background: var(--state-layer-hover);
  border-radius: var(--radius-control);
}
.cd-crumb-current {
  color: var(--color-text);
  font-weight: 600;
  cursor: default;
}
.cd-crumb-current:hover {
  background: transparent;
}
.cd-crumb-sep {
  color: var(--color-text-muted);
  padding: 0 2px;
}
.cd-crumb-edit {
  margin-left: auto;
  min-height: 40px;
  min-width: 40px;
  background: transparent;
  border: none;
  color: var(--color-text-muted);
  cursor: pointer;
  font-size: var(--font-size-base);
  flex-shrink: 0;
}
.cd-crumb-edit:hover {
  color: var(--color-text);
}
.cd-browser-dirs {
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-control);
  overflow: hidden;
}
.cd-dir-list {
  list-style: none;
  margin: 0;
  padding: var(--space-1) 0;
  max-height: 40vh;
  overflow-y: auto;
}
.cd-dir-row {
  display: grid;
  grid-template-columns: 32px minmax(0, 1fr) 24px;
  align-items: center;
  width: 100%;
  min-height: 40px;
  text-align: left;
  background: transparent;
  color: var(--color-text);
  border: none;
  padding: 0 var(--space-3);
  cursor: pointer;
  font-size: var(--font-size-base);
  font-family: var(--font-ui);
}
.cd-dir-row:hover {
  background: var(--state-layer-hover);
}
.cd-dir-icon {
  color: var(--color-text-muted);
}
.cd-dir-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
/* 存储根条目下的挂载点副标题（issues/15 S2）——两行式，副行更弱 */
.cd-dir-sub {
  display: block;
  color: var(--color-text-muted);
  font-family: var(--font-mono);
  font-size: var(--font-size-sm);
  opacity: 0.75;
}
/* issues/15 S2：读取目录中的 loading 态 */
.cd-browser-loading {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--space-2);
  padding: var(--space-4);
  color: var(--color-text-muted);
  font-size: var(--font-size-sm);
}
.cd-spinner {
  width: 16px;
  height: 16px;
  border: 2px solid var(--color-border);
  border-top-color: var(--color-indicator);
  border-radius: 50%;
  animation: cd-spin var(--motion-spin) linear infinite;
  flex-shrink: 0;
}
@keyframes cd-spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
/* issues/15 S3：未授权直读时的内联授权入口 */
.cd-grant {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: var(--space-2);
  padding: var(--space-4);
  background: var(--color-surface-raised);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-control);
  text-align: center;
}
.cd-grant-text {
  margin: 0;
  font-size: var(--font-size-md);
  color: var(--color-text);
}
.cd-grant-hint {
  margin: 0;
  font-size: var(--font-size-sm);
  color: var(--color-text-muted);
}
.cd-dir-arrow {
  color: var(--color-text-muted);
  justify-self: end;
}
.cd-browser-empty {
  padding: var(--space-4);
  color: var(--color-text-muted);
  font-size: var(--font-size-sm);
  text-align: center;
}
.cd-browser-footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: var(--space-2);
}
.cd-actions {
  display: flex;
  justify-content: flex-end;
  gap: var(--space-2);
  margin-top: var(--space-1);
}
.cd-actions .cd-btn {
  font-weight: 500;
  min-height: 40px;
}
</style>