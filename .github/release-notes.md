**Download the zip below** — portable, nothing to install (Windows 10/11, x64).
A lite build (`KoiCardTexTool-lite.exe`, ~1 MB, needs the .NET 8 Desktop Runtime) is inside the zip as well.

- three modes: batch (card / folder) · outfit card (per-part) · chara card
- lossless options: PNG recompression, optimal JPEG Huffman tables, grayscale to 2 channels
- exclusive-texture protection; presets reusable across other cards of the same outfit
- four UI languages (中文 / English / 日本語 / 한국어), light and dark themes
- CLI: `scan` / `parts` / `preset` / `compress` / `batch` — see `KoiCardTexTool.exe help`

Manuals are inside the zip (`docs/MANUAL-zh|en|ja|ko.md`); more documentation is in the repository.

---

### 本版更新 / What's new

**1.0.1** — 材质栏里的文字现在会跟着界面语言变了。

「单服装卡细分压缩」页部位表里的**材质**一列（「吊带」「翅膀」「珠宝」这些）之前在任何语言下都显示中文，
现已补齐 51 个材质名词条（英 / 日 / 韩）。英文界面下残留中文从 18 条降到 2 条
（剩下 2 条是语言下拉里各语言自己的名字）。

*The Material column in the parts table (strap, wings, jewel, …) now follows the UI language —
51 material-name entries were added with en/ja/ko translations.*

完整变更见 [CHANGELOG](https://github.com/EeEeX4/koikatsu-card-texture-tool/blob/main/CHANGELOG.md)。

---

### 中文说明

**下载上面的 zip**，解压即用（Windows 10/11 x64，不需要安装任何运行时）；
包里另有轻量版 `KoiCardTexTool-lite.exe`（约 1 MB，需要 .NET 8 桌面运行时）。

三种模式（批量 / 按部位 / 人物卡）· 无损压缩选项（PNG 再压缩、JPEG 最优哈夫曼、灰度降 2 通道）·
独占贴图保护 · 预设可跨卡复用 · 四种界面语言 · 浅色与深色主题。
命令行：`scan` / `parts` / `preset` / `compress` / `batch`。

使用手册在 zip 的 `docs/` 目录（四种语言）。

> ⚠️ 非官方第三方工具，与游戏厂商无关。**压缩前请务必备份原卡。**
