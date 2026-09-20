<script setup lang="ts">
/**
 * ChangeDirDialog.vue — 「更改目录」对话框（手动输入路径 + 页内目录浏览）。
 *
 * **现为 Web 专用**（issues/15 方案 B）：MAUI 的目录入口唯一——「更改目录」直接弹原生选择器
 * （Windows FolderPicker / Android 系统选择器），选完由 C# 判定快速/兼容轨，本对话框在 MAUI
 * 上不再被打开。故此处只保留桌面浏览器真正会走的那条路：
 *
 * - 手动输入路径（粘贴是正经用法）+ 「浏览…」逐层列举（`source.listDirs` → server `/game/dirs`）；
 * - 面包屑导航（「上级 / 前往」收编为可点段）；
 * - 浏览中确认（「扫描此目录」）→ `source.scan(browsePath)` 并关闭，传输失败时保留框体。
 *
 * 历史上它还承担过两件事，随入口收敛一并删除：存储根清单（挂载点由 C# 给的换卷入口）与
 * 「未授权直读」的内联授权 CTA——两者都只在 Android 可达。
 * 剩余几处 `isAndroidMaui` 显隐守卫保留为**意图声明**（Android 上不该出现手动输入框）。
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
} from '../lib/gameLibrary';

const props = defineProps<{
  /** 是否显示。 */
  visible: boolean;
  /** 游戏库数据源（当前恒为 http；见文件头——本对话框为 Web 专用）。 */
  source: GameLibrarySource;
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
      localError.value = null;
      previouslyFocused = document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null;
      await nextTick();
      // Web 以手动输入（粘贴路径）为主，打开即聚焦。
      inputRef.value?.focus();
    } else if (previouslyFocused && previouslyFocused.isConnected) {
      previouslyFocused.focus();
      previouslyFocused = null;
    }
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
/**
 * 是否为可直接浏览的**真实路径**。
 *
 * `content://`（SAF）与空串都不是——页内浏览器没有可用起点（本组件现为 Web 专用，不再有
 * 挂载点清单可退，只能提示用户先填一个真实路径）。判定宽松（只要有根形态即可），
 * 真正能否列举由 `source.listDirs` 说了算。
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

/** 进入目录浏览指定路径（逐层列举；起点由调用方给）。 */
async function enterBrowse(dir: string): Promise<void> {
  const target = dir.trim();
  // 非真实路径（空 / content://）没有可浏览的起点，先提示填绝对路径。
  // 这原是「退到存储根清单」那条路，随存储根清单一并删除——Web 上本就落到同一句提示。
  if (!isRealPath(target)) {
    browseDirs.value = [];
    localError.value = '无法读取存储位置——请在上方直接输入目录路径。';
    return;
  }
  browsePath.value = target;
  browseLoading.value = true;
  // listDirs 可选——不支持页内浏览的数据源返回 undefined，此处归一为 null（走同一条错误分支）
  const result: DirListResult | null = await (props.source.listDirs?.(target) ?? null);
  browseLoading.value = false;
  if (result === null) {
    browseDirs.value = [];
    localError.value = `无法访问目录：${target || '(空)'}`;
    return;
  }
  browsePath.value = result.currentPath;
  browseDirs.value = result.dirs;
  localError.value = null;
}

/** 点击子目录——进入该目录继续浏览。 */
async function onPickDir(name: string): Promise<void> {
  const base = browsePath.value.replace(/[\\/]+$/, '');
  const sep = browsePath.value.includes('\\') ? '\\' : '/';
  await enterBrowse(name ? `${base}${sep}${name}` : base);
}

/**
 * 面包屑段（UX 改版）：由 browsePath 切分而来，取代「上级 / 路径输入框 / 前往」三件套。
 *
 * 按分隔符逐段展开（Windows 先给 `D:\` 盘符段，Unix 先给 `/` 根段）；
 * 末段是当前位置，只读加粗不可点，点其余任意段 = 直接跳回该层（「上级」是其特例）。
 *
 * 历史上还有两种段型，随存储根清单一并删除（都只在 Android 可达）：**卷选择段**
 * （多卷换卷入口）与**根前缀折叠**（把 `/storage/emulated` 折成一个 💾 卷名段）。
 */
const crumbs = computed<{ label: string; path: string }[]>(() => {
  const p = browsePath.value.trim();
  if (!p) return [];
  const sep = p.includes('\\') ? '\\' : '/';
  const norm = p.replace(/[\\/]+$/, '');
  const out: { label: string; path: string }[] = [];
  let consumed = '';
  if (sep === '\\') {
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
onUnmounted(() => document.removeEventListener('keydown', onDocKeydown));
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
          <template v-if="browsing">选择包含游戏的目录后点「扫描此目录」。</template>
          <template v-else>输入游戏主目录（可包含多个游戏），或点「浏览…」逐层选择。</template>
        </p>

        <!-- 手动路径输入（浏览展开时隐藏——路径信息由面包屑唯一呈现，消除三处重复；
             Android 下彻底隐藏——软键盘输长路径是折磨，但该分支现已不可达：
             本对话框在 MAUI 上不再打开，保留守卫作意图声明） -->
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

        <!-- 页面内目录浏览（面包屑导航，走 source.listDirs） -->
        <div v-if="browsing" class="cd-browser">
          <!--
            面包屑（UX 改版）：上级 / 前往 两按钮 + 路径行的合体替代。
            点任意父段跳回该层；点 ⌨ 回手动输入。
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
                :title="c.path"
                @click="enterBrowse(c.path)"
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

          <div v-else class="cd-browser-dirs">
            <ul v-if="browseDirs.length" class="cd-dir-list">
              <li v-for="d in browseDirs" :key="d">
                <button type="button" class="cd-dir-row" @click="onPickDir(d)">
                  <span class="cd-dir-icon" aria-hidden="true">📁</span>
                  <span class="cd-dir-name">{{ d }}</span>
                  <span class="cd-dir-arrow" aria-hidden="true">›</span>
                </button>
              </li>
            </ul>
            <div v-else class="cd-browser-empty">
              此目录下没有子目录
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