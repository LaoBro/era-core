/**
 * 纵向撑高规则的单测（纯函数 + 一个几何模型）。
 *
 * 为什么这个文件必须存在：组件测试（happy-dom）没有布局，测不到「两张热区盒到底有没有
 * 重叠」——而这正是 v2 初版漏掉的那类错误（真机上表现为相邻按钮部分重叠）。所以这里用
 * 一个两行的几何模型（盒 = [行顶 - 半边增量, 行顶 + 基准行高 + 半边增量]，与
 * `.term-btn-touch` 的 CSS 同义）把「永不重叠」当成不变量穷举验证。
 *
 * 模型偏离 CSS 的风险由 spec 的 Testing Decisions 显式承担：组件测试锁「内联注入了什么」，
 * 本文件锁「按该注入算出来的几何自洽」。
 */
import { describe, it, expect } from 'vitest';
import { computeLineExtras } from '../touchLayout';
import type { DisplayLine } from '../../types/protocol';

const TARGET_PX = 48;
/** 1 列 = 1 个半角字宽；按钮默认 2 列宽（一个全角字）。 */
const BTN_WIDTH = 2;

function blank(): DisplayLine {
  return { entries: [{ segments: [{ text: '　' }], button: null }], align: 'left', isLineEnd: true } as DisplayLine;
}

function buttonLine(col: number, opts: { width?: number; generation?: number } = {}): DisplayLine {
  return {
    entries: [
      {
        segments: [{ text: '選項' }],
        button: {
          value: 0,
          isInteger: true,
          generation: opts.generation ?? 0,
          col,
          width: opts.width ?? BTN_WIDTH,
        },
      },
    ],
    align: 'left',
    isLineEnd: true,
  } as DisplayLine;
}

/** 一枚测不出几何的按钮（脏数据）：col/width 缺失。 */
function geometryLessButtonLine(): DisplayLine {
  return {
    entries: [{ segments: [{ text: '選項' }], button: { value: 0, isInteger: true, generation: 0 } }],
    align: 'left',
    isLineEnd: true,
  } as DisplayLine;
}

const spansOf = (l: DisplayLine): Array<[number, number]> =>
  l.entries
    .filter((e) => e.button)
    .map((e) => [e.button!.col as number, (e.button!.col as number) + (e.button!.width as number)]);

const overlaps = (a: Array<[number, number]>, b: Array<[number, number]>): boolean =>
  a.some(([as, ae]) => b.some(([bs, be]) => as < be && bs < ae));

function extrasOf(lines: readonly DisplayLine[], r: number): ReadonlyMap<number, number> {
  return (
    computeLineExtras(lines, {
      rowHeightPx: r,
      targetRowPx: Math.max(r, TARGET_PX),
      isLive: (e) => e.button != null,
    }) ?? new Map<number, number>()
  );
}

describe('computeLineExtras: 直接取值', () => {
  it('相邻两行同列 → 只有**上方**那行撑高，且是满额（默认行距 19 → 29px）', () => {
    const extras = extrasOf([buttonLine(0), buttonLine(0)], 19);
    expect(extras.get(0)).toBe(29);
    expect(extras.has(1)).toBe(false);
  });

  it('隔 1 行同列 → 上方那行只撑几何上必需的 10px（不是 29px）', () => {
    const extras = extrasOf([buttonLine(0), blank(), buttonLine(0)], 19);
    expect(extras.get(0)).toBe(10);
    expect(extras.has(2)).toBe(false);
  });

  it('隔 2 行同列 → 完全不用撑（48px 盒相距 57px，本就挨不到）', () => {
    const extras = extrasOf([buttonLine(0), blank(), blank(), buttonLine(0)], 19);
    expect(extras.size).toBe(0);
  });

  it('下方行因相邻冲突撑高时，上方同列行也必须撑高——否则两盒叠 10px（真机回归）', () => {
    // 行 2 与行 3 相邻冲突 → 行 2 撑满额；行 0 与行 2 隔 1 行同列 → 行 0 也得撑 10px
    const extras = extrasOf([buttonLine(0), blank(), buttonLine(0), buttonLine(0)], 19);
    expect(extras.get(2)).toBe(29);
    expect(extras.get(0)).toBe(10);
  });

  it('列区间不重叠 → 一行都不撑（纵向重叠无所谓，点不到隔壁）', () => {
    expect(extrasOf([buttonLine(0), buttonLine(10)], 19).size).toBe(0);
    expect(extrasOf([buttonLine(0), blank(), buttonLine(10)], 19).size).toBe(0);
  });

  it('三行连排同列 → 只有前两行撑高，总高省下一整行（+29px × 2 而非 × 3）', () => {
    const extras = extrasOf([buttonLine(0), buttonLine(0), buttonLine(0)], 19);
    expect(extras.get(0)).toBe(29);
    expect(extras.get(1)).toBe(29);
    expect(extras.has(2)).toBe(false);
  });

  it('失效按钮不参与：同一位置换成历史回合按钮 → 不撑', () => {
    const lines = [buttonLine(0), blank(), buttonLine(0, { generation: 9 })];
    const extras = computeLineExtras(lines, {
      rowHeightPx: 19,
      targetRowPx: TARGET_PX,
      isLive: (e) => e.button?.generation === 0,
    });
    expect(extras).toBe(null);
  });

  it('缺 col/width 的存活按钮视为占满整行 → 与窗口内任何按钮都冲突', () => {
    const extras = extrasOf([buttonLine(0), blank(), geometryLessButtonLine()], 19);
    expect(extras.get(0)).toBe(10);
  });

  it('游戏自身行距已达目标 → 返回 null（走虚拟滚动定高快路径）', () => {
    expect(extrasOf([buttonLine(0), buttonLine(0)], 60).size).toBe(0);
    expect(computeLineExtras([buttonLine(0)], { rowHeightPx: 60, targetRowPx: 60, isLive: () => true })).toBe(null);
  });

  it('无按钮的行不产生任何条目', () => {
    expect(extrasOf([blank(), blank()], 19).size).toBe(0);
  });

  it('窗口随行距缩放：行距 10px 时 W = 4，隔 3 行仍需撑 (48-10) - 3×10 = 8px', () => {
    const gap = (n: number) => [buttonLine(0), ...Array.from({ length: n }, () => blank()), buttonLine(0)];
    expect(extrasOf(gap(3), 10).get(0)).toBe(8); // 行索引差 4
    expect(extrasOf(gap(4), 10).size).toBe(0); // 行索引差 5 → 超出窗口
  });
});

// ---------- 不变量：列区间重叠的两行按钮盒永不重叠 ----------

/** 穷举小布局：返回所有「列区间重叠、但按规则算出的盒也重叠」的配置。 */
function findOverlaps(rows: number, rList: number[], colChoices: number[]) {
  const violations: string[] = [];
  const subsets = (n: number, size: number): number[][] => {
    const out: number[][] = [];
    const rec = (start: number, acc: number[]) => {
      if (acc.length === size) { out.push([...acc]); return; }
      for (let i = start; i < n; i++) rec(i + 1, [...acc, i]);
    };
    rec(0, []);
    return out;
  };

  for (const r of rList) {
    const target = Math.max(r, TARGET_PX);
    if (target <= r) continue;
    const half = (target - r) / 2;
    for (const size of [2, 3]) {
      for (const idxs of subsets(rows, size)) {
        const combos: number[][] = [];
        const rec = (k: number, acc: number[]) => {
          if (k === idxs.length) { combos.push([...acc]); return; }
          for (const c of colChoices) rec(k + 1, [...acc, c]);
        };
        rec(0, []);
        for (const cols of combos) {
          const lines = Array.from({ length: rows }, () => blank());
          idxs.forEach((li, n) => { lines[li] = buttonLine(cols[n]); });

          const extras = extrasOf(lines, r);
          const tops: number[] = [];
          let y = 0;
          for (let i = 0; i < rows; i++) { tops.push(y); y += r + (extras.get(i) ?? 0); }
          const box = (i: number) => [tops[i] - half, tops[i] + r + half] as const;

          for (let a = 0; a < idxs.length; a++) {
            for (let b = a + 1; b < idxs.length; b++) {
              const i = idxs[a];
              const j = idxs[b];
              if (!overlaps(spansOf(lines[i]), spansOf(lines[j]))) continue;
              const [aTop, aBottom] = box(i);
              const [bTop, bBottom] = box(j);
              const overlap = Math.min(aBottom, bBottom) - Math.max(aTop, bTop);
              if (overlap > 0.01) {
                violations.push(
                  `r=${r} [行${i} col${cols[a]} ↔ 行${j} col${cols[b]}] k=${j - i} 叠 ${overlap.toFixed(1)}px`,
                );
              }
            }
          }
        }
      }
    }
  }
  return violations;
}

describe('computeLineExtras: 不变量（盒高恒为目标高度，列区间重叠者永不重叠）', () => {
  it('穷举 6 行以内、2~3 个按钮、多种列距与行距 → 零重叠', () => {
    const violations = findOverlaps(6, [12, 14, 16, 18, 19, 20, 22, 24, 28, 32], [0, 3, 8]);
    expect(violations).toEqual([]);
  });
});
