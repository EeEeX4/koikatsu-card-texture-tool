**Download the zip below** — portable, nothing to install (Windows 10/11, x64).
A lite build (`KoiCardTexTool-lite.exe`, ~1 MB, needs the .NET 8 Desktop Runtime) is inside the zip as well.

- three modes: batch (card / folder) · outfit card (per-part) · chara card
- **texture export** in the original format plus a `manifest.csv` (usage tagged clothes / accessory / body / unreferenced)
- **texture removal** with binding cleanup — saves a new card and verifies the output before writing
- lossless options: PNG recompression, optimal JPEG Huffman tables, grayscale to 2 channels
- exclusive-texture protection; presets reusable across other cards of the same outfit
- four UI languages (中文 / English / 日本語 / 한국어), light and dark themes
- CLI: `scan` / `parts` / `preset` / `compress` / `batch` / `extract` / `delete` — see `KoiCardTexTool.exe help`

Manuals are inside the zip (`docs/MANUAL-zh|en|ja|ko.md`); more documentation is in the repository.

---

### 本版更新 / What's new

**1.2.0** — 贴图导出 / 用途识别 / 删除贴图。

- **批量提取贴图**：新命令 `extract`；第 1 页「导出贴图…」支持单卡或整个文件夹，第 2/3 页支持「导出选中 / 导出全部」。保持原格式（PNG/JPEG/HDR/EXR），按卡分目录并生成 `manifest.csv`（TexID、类型、用途、尺寸、字节、槽位、材质、属性、文件）。
- **用途识别**：每张贴图标为衣服 / 饰品 / 身体 / 未引用；贴图明细新增「用途」列与「全选」按钮；可按用途过滤导出。
- **删除选中贴图**：多选后右键或点「删除选中…」，物理删除 `TextureDictionary` 条目并清理 `MaterialTexturePropertyList` 引用，另存新卡（默认不覆盖原卡）；写出前自动复检。
- 命令行新增 `extract` / `delete`；`extract` 支持 `purpose=` / `format=png` / `overwrite=`，`delete` 支持 `texids=6,7` 或 `texids=orphans`。

*Texture export / usage detection / texture removal — see CHANGELOG for details.*

完整变更见 [CHANGELOG](https://github.com/EeEeX4/koikatsu-card-texture-tool/blob/main/CHANGELOG.md)。

---

### 中文说明

**下载上面的 zip**，解压即用（Windows 10/11 x64，不需要安装任何运行时）；
包里另有轻量版 `KoiCardTexTool-lite.exe`（约 1 MB，需要 .NET 8 桌面运行时）。

三种模式（批量 / 按部位 / 人物卡）· 贴图导出（原格式 + `manifest.csv`）· 用途识别（衣服 / 饰品 / 身体 / 未引用）·
选中贴图删除（清理绑定、另存新卡）· 无损压缩选项（PNG 再压缩、JPEG 最优哈夫曼、灰度降 2 通道）·
独占贴图保护 · 预设可跨卡复用 · 四种界面语言 · 浅色与深色主题。
命令行：`scan` / `parts` / `preset` / `compress` / `batch` / `extract` / `delete`。

使用手册在 zip 的 `docs/` 目录（四种语言）。

> ⚠️ 非官方第三方工具，与游戏厂商无关。**压缩或删改前请务必备份原卡，并先用测试卡进游戏验证。**
