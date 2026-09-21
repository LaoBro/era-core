/**
 * prewarmMenuShadow —— 首次弹出菜单卡顿的一次性预热（见 .scratch/menu-first-open-jank/spec.md）。
 *
 * 背景：DrawerMenu / PopupMenu 共用 `box-shadow: var(--elevation-menu)`（含 8px 模糊半径）。
 * 首次把这个柔和阴影画到屏幕上时，GPU 要现场编译高斯模糊 shader——这一步离主线程，
 * 挤在玩家首次点开菜单的那几帧里，表现为「点了之后菜单『啪』地出现，看不到出场动画」；
 * 第二次起 shader 已缓存（进程级），就流畅了。
 *
 * 思路：页面空闲时挂一个「不可见但真的被绘制一次」的探针，把首次要现场编译的 GPU 程序
 * 提前编译掉，然后立刻移除。
 *
 * 探针要复刻真实抽屉的全部材质，而不只是阴影：
 * - `box-shadow: --elevation-menu` —— 高斯模糊 shader（第一段，实测已消除大头）；
 * - 圆角 + `overflow-y:auto` 的可滚动层 —— 圆角裁剪材质（第二段，实测首开/首次关闭
 *   仍各留一小段丢弃帧，怀疑是这一类）；
 * - 全屏半透明 scrim —— `--color-scrim-menu` 的合成材质；
 * - 一行带内描边 SVG 图标的菜单项 —— 主界面不画这些图标，首次栅格化会各compile一次。
 *
 * 因为 shader 缓存是进程级的，一次预热同时覆盖浮层、抽屉、以及列表每行那个共用
 * `--elevation-menu` 的删除菜单——不要为每个菜单各预一次。
 *
 * 必须非零透明度：opacity:0 / display:none / scale(0) / visibility:hidden 都让浏览器
 * 跳过绘制，shader 从没被编译过，预热等于白做。只有 opacity:0.01 才让栅格化真的发生。
 *
 * probe 必须留在视口内（right:0; top:0），不能 top:-9999px——离屏元素被合成器裁剪，
 * 不参与栅格化，编译不出来。
 */
let prewarmed = false;

/**
 * 预热一次（幂等）。在 App 挂载后空闲期调用，避免与首屏抢主线程。
 */
export function prewarmMenuShadow(): void {
  if (prewarmed) return;
  prewarmed = true;
  const run = () => requestAnimationFrame(() => requestAnimationFrame(paintProbe));
  if (typeof (window as any).requestIdleCallback === 'function') {
    (window as any).requestIdleCallback(run, { timeout: 2000 });
  } else {
    setTimeout(run, 1500); // 老 WebView 无 requestIdleCallback 的兜底
  }
}

/** 复刻 DrawerMenu.vue 抽屉材质的一行内描边 SVG 图标（保持与组件同一画风，命中同一路径程序）。 */
const PREWARM_ICON = '<svg width="20" height="20" viewBox="0 0 24 24" aria-hidden="true">'
  + '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>'
  + '</svg>';

function paintProbe(): void {
  if (!document.body) return;
  const probe = document.createElement('div');
  probe.setAttribute('aria-hidden', 'true');
  // 整体 opacity:0.01 → 整个子树（scrim/抽屉/图标）都真的被绘制一次，但肉眼不可见
  probe.style.cssText = [
    'position: fixed', 'inset: 0', 'z-index: 1',
    'opacity: 0.01',
    'pointer-events: none',
    'visibility: visible',
  ].join('; ');
  probe.innerHTML =
    // scrim：全屏半透明合成材质
    '<div style="position:absolute;inset:0;background:var(--color-scrim-menu)"></div>'
    // 抽屉面板：圆角 + overflow 裁剪 + 阴影 + 标题 + 一行带图标菜单项，复刻 .drawer
    + '<aside style="'
    + 'position:absolute;top:0;bottom:0;right:0;z-index:900;width:320px;max-width:100%;'
    + 'background:var(--color-surface-raised);box-shadow:var(--elevation-menu);'
    + 'border-radius:var(--radius-surface) 0 0 var(--radius-surface);'
    + 'padding:var(--space-2);display:flex;flex-direction:column;overflow-y:auto;'
    + 'touch-action:pan-y;'
    + '">'
    + '<div style="display:flex;align-items:center;min-height:var(--touch-target);'
    + 'padding:var(--space-2) var(--space-3);border-bottom:1px solid var(--color-border);'
    + 'margin-bottom:var(--space-1)"><span>预热</span></div>'
    + '<button type="button" style="display:flex;align-items:center;gap:var(--space-3);'
    + 'min-height:var(--touch-target);background:transparent;color:var(--color-text);'
    + 'text-align:left;padding:var(--space-2) var(--space-3);'
    + 'border-radius:var(--radius-control);font-family:var(--font-ui);border:none;">'
    + '<span style="width:20px;display:inline-flex;align-items:center;justify-content:center;'
    + 'color:var(--color-text)">' + PREWARM_ICON + '</span>'
    + '<span>预热项</span>'
    + '</button>'
    + '</aside>';
  document.body.appendChild(probe);
  // 给合成器两三帧把这条层真的画掉，再移除；不留在屏幕上
  requestAnimationFrame(() => requestAnimationFrame(() => probe.remove()));
}