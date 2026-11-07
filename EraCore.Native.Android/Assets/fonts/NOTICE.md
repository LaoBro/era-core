# Native 网格字体资产说明

本目录包含 Native（Android）渲染使用的三支内置字体。除 `EraCoreCJK.ttf` 外，均随 APK 打包。

| 文件 | 用途 | 覆盖范围 | 来源 / 许可 |
| --- | --- | --- | --- |
| `EraGridMono.ttf` | 字符网格主体：ASCII 与全角符号 | `U+0020–U+007E`、`U+2500–U+26FF`、`U+3000`（源字体实有 179 码位） | IPAGothic 的**派生程序**（子集 + 改名），IPA Font License v1.0，见下 |
| `EmueraBlock.ttf` | 网格补充：补齐 IPAGothic 缺失的半角符号（Block Elements 等） | Box 96 + Block 32 + Geometric 57 + Misc 80（共 265） | 由 DejaVu Sans（Bitstream Vera 衍生）提取并缩放，Bitstream Vera 许可，见下 |
| `EraCoreCJK.ttf` | 中文 / 日文汉字等 CJK 字形 | Noto Sans SC 子集（既有资产，未改动） | Noto Sans SC，SIL Open Font License 1.1 |

解析顺序（逐码位回退）：`ASCII/符号 → EraGridMono → EmueraBlock → EraCoreCJK → 系统字体`。

## EraGridMono：IPAGothic 的派生程序

- 原始字体：IPAGothic（IPAフォント / IPA Font），版权归 IPA（Information-technology Promotion Agency, Japan）所有。
  官方发布页：<https://moji.or.jp/ipafont/>（下载 IPAGothic 原版 ttf）。
- 派生方式：仅保留字符网格需要的码位（`U+0020–U+007E` + `U+2500–U+26FF` + `U+3000`），
  丢弃 GSUB/GPOS，并将字体家族名 / 全名 / PostScript 名改为 `EraGridMono`（**不含 "IPA"**）。
  生成脚本 `.scratch/native-m3/gen_grid_fonts.py`（一次性工具，不入库）：
  `fontTools.subset` 取码位 → `flavor = None` 输出 ttf → 重写 name 表。
- 许可证：本目录 `IPA_Font_License_Agreement_v1.0.txt` 为 IPA Font License v1.0 全文，随本派生字体一并分发。
  依第 3.1 条，派生程序须（a）附带契约全文、（b）提供以原版替换派生版的手段、（c）采用与原版不同的名称。
  本文件与同目录契约即为（a）（c）的落实；（b）的落实方式见下。

### 如何用原版替换派生版

派生版不是运行必需品，可随时换回原版：

1. 从 <https://moji.or.jp/ipafont/> 下载 IPAGothic 原版（`ipag.ttf`）并接受 IPA Font License v1.0。
2. 用本目录同款解析顺序覆盖资产即可：把原版 ttf 重命名为 `EraGridMono.ttf` 放回本目录，
   或保留原名并修改 `NativeFontManager` 的 `GridAssetPath` 指向它。
3. 重新构建 APK。字体身份不变（仍是「网格字体源」），无需改动代码或缓存键。
4. 原版为全量字体（约 5.9 MB ttf），替换后 APK 体积会增加约 5.9 MB；渲染行为与派生版一致
   （ASCII / 全角符号 advance 均为 0.5em / 1em，与 ＭＳ ゴシック 同口径）。

## EmueraBlock：Bitstream Vera 衍生字体

- 来源：从 DejaVu Sans 2.37（<https://github.com/dejavu-fonts/dejavu-fonts/releases>）提取
  IPAGothic 缺失的四区符号（Box / Block / Geometric / Misc，265 字符）。提取时以 analyze 脚本测得的
  ＭＳ ゴシック 实测 bbox 为目标矩形做仿射缩放，advance 强制为 ＭＳ ゴシック 实测值（多为 0.5em）。
- 生成方式与 Web 完全一致：`EraCore.Web/scripts/build_emblock_font.py`（输出 woff2），
  本目录 ttf 仅换容器格式（`TTFont(...).flavor = None` 后另存），字形未改。
- 许可：DejaVu Sans 沿用 Bitstream Vera Fonts 版权与自由许可，允许嵌入与再分发。
  版权声明：`Copyright (c) 2003 by Bitstream, Inc. All Rights Reserved. Bitstream Vera is a trademark of Bitstream, Inc.`
  许可全文（Bitstream Vera Fonts Copyright）：

  ```
  Permission is hereby granted, free of charge, to any person obtaining a copy of the fonts
  accompanying this license ("Fonts") and associated documentation files (the "Font Software"),
  to reproduce and distribute the Font Software, including without limitation the rights to use,
  copy, merge, publish, distribute, and/or sell copies of the Font Software, and to permit persons
  to whom the Font Software is furnished to do so, subject to the following conditions:

  The above copyright and trademark notices and this permission notice shall be included in all
  copies of one or more of the Font Software typefaces.

  The Font Software may be modified, altered, or otherwise transformed, and in this respect the
  license grant above includes the right to create derivative works, provided that the names
  "Bitstream" and "Vera" are not used in the name of any modified version.

  The Font Software may be sold as part of a larger software package but no copy of one or more
  of the Font Software typefaces may be sold by itself.

  THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
  INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
  PURPOSE AND NONINFRINGEMENT OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL
  BITSTREAM OR THE GNOME FOUNDATION BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
  INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL DAMAGES, WHETHER IN AN
  ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF THE USE OR INABILITY TO USE THE
  FONT SOFTWARE OR FROM OTHER DEALINGS IN THE FONT SOFTWARE.
  ```

## 体积（构建期实测）

| 资产 | ttf | deflate（APK 内近似） |
| --- | --- | --- |
| `EraGridMono.ttf` | 27,892 B | 17,322 B |
| `EmueraBlock.ttf` | 31,328 B | 19,628 B |
| 合计新增 | 59,220 B ≈ 57.8 KB | 36,950 B ≈ 36.1 KB |
