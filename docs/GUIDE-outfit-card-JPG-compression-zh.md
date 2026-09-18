# Koikatsu 衣服卡里的贴图能不能压成 JPG？——实测结论

问题：「可以压缩成JPG格式吗」，对象是 `CardA.png`（KoiKatu 衣服卡，
177,381,281 B，贴图藏在 IEND 之后 MaterialEditor 的 `TextureDictionary` 里）。

**答案：可以，但有三个硬约束——所以不能"全转 JPG"，要按绑定用途分流。**

---

> 说明：本文是开发期的实测记录，文中的路径为当时的工作区结构，仅供参考。

## 1. 实测四档结果（同一张卡，34 张贴图全部 ≤1024）

| 方案 | 卡文件 | 相对原卡 | 贴图载荷 | 说明 |
|---|---|---|---|---|
| 原卡（4096/2048） | 177,381,281 B | — | 176.8 MB | 参考 |
| 全 PNG 1024 | 19,013,639 B | **-89.3%** | 18.4 MB | 无损，alpha 全保 |
| **auto：11/34 转 JPEG q90** | **13,662,388 B** | **-92.3%** | 12.5 MB | 只压"安全"的（推荐折中） |
| auto + 法线/遮罩降到 512 | **4,602,667 B** | **-97.4%** | 4.03 MB | 体积最小且 alpha 安全（推荐分享用） |
| 全 34 张 JPEG q90 | 4,566,888 B | -97.4% | 3.99 MB | **会破坏 alpha，仅作下限参考** |

产物都在 `_work\kkcard\out\`：
`... (tex1024).png`、`... (tex1024-jpgauto).png`、`... (jpgauto+mask512).png`、`... (tex1024-jpgall).png`。
四份都通过 `kkcard_verify.py` 结构+解码校验（PASS）。

## 2. 为什么不能全转 JPG —— 三个硬约束

1. **JPEG 没有 alpha 通道**。这卡 34 张里 10 张 alpha 真的有内容：
   `1, 5, 7, 12, 15, 18, 22, 26, 27, 34`。其中 **`tex_27` 是 `MainTex`（衣服主贴图）且 57.5% 像素透明**
   —— 那是镂空/花边轮廓，转 JPEG 后整张变成不透明，衣服轮廓会糊成一块。
2. **法线贴图（`BumpMap`）怕 JPEG 振铃**。本卡 9 张绑定 `BumpMap`（`1,5,7,12,15,18,22,26,34`），
   JPEG 有损误差会被法线直接放大成明暗色带/脏边。它们本来就是数据贴图，PNG 更合适。
3. **遮罩类（`ShadowBorderMask` / `Main2ndBlendMask`）同理**：灰度 mask 上的 JPEG 噪声会变成
   阴影边界的条带，而且这类图 JPEG 往往**压不动甚至变大**（如 `tex_8` 16,340→20,411 B，`tex_35` 22,359→32,379 B）。
   所以脚本里"JPEG 没比 PNG 小就退回 PNG"是必需的。

因此 `auto` 模式只把 **11 张** 真正划算的贴图转 JPEG：
`2, 3, 9, 10, 13, 16, 19, 23, 29, 36, 38` —— 全是 `MainTex` / `MatCapTex`（RGB 无反照度数据）或 alpha 恒 255 的。

## 3. 允许 JPEG 的依据（解码路径已确认）

- 卡片 `TextureDictionary` 存的是**原始图片字节**（原来全是 PNG）。`KK_MaterialEditor.dll`
  （`E:\Koikatu-Game\BepInEx\plugins\KK_Plugins\KK_MaterialEditor.dll`，552,448 B）里同时存在
  `LoadImage` 成员引用（1 处）和 `EncodeToPNG`（1 处）：
  读取走 Unity 的 `LoadImage`，**该 API 自动识别 PNG/JPG**，所以塞 JPEG 字节能解码；
  存卡走 `EncodeToPNG`，即**从卡里另存一次会变回 PNG**（JPG 只是存储/分享格式，不影响运行时格式）。
- 容器侧无需改动：外部 MessagePack 结构不变，只有一个 `bin32` 内容变了（外科式替换）。

**未验证的部分（需你在游戏里过一眼）**：没有无头方式跑 MaterialEditor，所以只做了
容器结构 + 图片可解码 + 部件 ID/尾部数据不变的校验，**渲染效果要进游戏读一次这张卡确认**。
出问题时材质会变粉/丢贴图，把原卡换回去即可（原卡未改动）。

## 4. 命令

```powershell
cd _work\tex
# 折中：只对安全贴图用 JPEG q90，其余 1024 PNG
python kkcard_repack.py 'CardA.png' 'out\a.png' 1024 auto 90
# 分享用最小体积：法线/遮罩/alpha 贴图降到 512，其余 JPEG
python kkcard_repack.py 'CardA.png' 'out\b.png' 1024 auto 90 512
# 只缩不转（无损）：mode 省略或写 png
python kkcard_repack.py 'CardA.png' 'out\c.png' 1024
# 全 JPEG（会丢 alpha，慎用）：写 jpg
python kkcard_repack.py 'CardA.png' 'out\d.png' 1024 jpg 90
# 校验（结构 + 每张贴图可解码 + 格式/尺寸/alpha 范围）
python kkcard_verify.py 'out\b.png'
```

参数：`<in> <out> <max_size> [png|jpg|auto] [quality] [mask_size]`。
`mask_size` = 必须留 PNG 的那批贴图自己的上限（默认与 `max_size` 相同）。

## 5. auto 的判定规则（`kkcard_repack.py`）

```
use_jpg = alpha 全 255（无 alpha 或 alpha 恒不透明） 且 不绑定 BumpMap/*Mask
之后：jpeg 字节数 >= png 字节数   -> 退回 PNG
      tex_38 这类 alpha 恒 255 的 RGBA 也走 JPEG（安全）
```

绑定关系从卡内 `MaterialTexturePropertyList`（74 条）现读，不写死 TexID。

## 6. 质量参考（JPEG q90 vs 同尺寸 PNG，RGB PSNR）

绝大多数 41~57 dB（`tex_18` 41.4、`tex_26` 41.6、`tex_5` 42.7 最低；`tex_8` 81、`tex_21` 76.6 最高）
—— 1024 尺寸下 `MainTex` 用 q90 肉眼看基本无损；要更保真就 `auto 95`（实测 14,212,087 B / 13.6 MB 载荷）。

## 7. 坑

- JPEG 的 `subsampling=0`（4:4:4）必须显式给，否则 Pillow 默认 4:2:0 会让衣服上的细线/文字发虚。
- `tex_8/21/25/35/39` 这类近乎纯色的 RGB 图，JPEG 反而更大 → 必须做"不划算就退回 PNG"的判断。
- 灰度 `L`/`LA` 图存 JPEG 要用 `convert('L')`（灰度 JPEG），转 RGB 会白涨 3 倍体积。
- `LA` 若 alpha 有内容，JPEG 无处可放 → 只能 PNG（本卡 `tex_11` alpha 恒 255，可转）。
- 卡片文件名含 `&`、`空格`、`()`，PowerShell 里一律加引号；前面 zipmod 那边含 `[ ]` 的要用 `-LiteralPath`。

---

## 8. 别的服装卡能用吗？——能，已批量实测

脚本已通用化（不再假设 9 个部件、不再假设 34 贴图、不再假设贴图是 PNG/1024 以内）：

| 通用化改动 | 原因 |
|---|---|
| `png_end()` 按 chunk 长度遍历找 IEND | 原来 `mm.find(b'IEND')` 会被 IDAT 压缩数据里的偶然字节骗到 |
| `find_key()` 按 fixstr/str8/16/32 长度前缀定位键 | 不同 MaterialEditor 版本键前缀可能不同 |
| 无 `TextureDictionary` → 原样复制 + rc=3 | 大部分普通卡没有自定义贴图 |
| `P`/`PA`/`1` 模式先展开再缩放，P 源缩放后重新量化调色板 | 调色板图直接转 RGB 会暴涨（实测 98.7%→84.7%） |
| 单张贴图只要"重编码不更小"就**原样透传**（`orig`） | 保证任何卡**永远不会变大**（实测 104.8%→99.9%） |
| 部件 ID 不再校验必须 9 个 | 卡型不同部件数不同 |
| stdout 改 UTF-8（errors=replace） | 卡名里有 U+200E 之类字符会让 GBK 控制台崩 |

### 本机实测

`E:\Koikatu-Game\UserData\coordinate`（60+ 张卡）盘点：绝大多数卡带 1~52 张贴图，尺寸 1024~4096，
格式除 `PNG/RGBA`、`PNG/RGB`、`PNG/P`（调色板）外，**已有若干卡里本来就是 `JPEG/RGB` blob**
（`108814349_p12`、`118563740_p3`）——即 JPG 贴图在真实流传的卡里已经存在且能被游戏加载。

9 张样本批量（`mode=auto 90`，上限 1024）：**100,514,734 → 24,392,726 B（24.3%），9/9 PASS**。
逐卡：`106737423_p8` 25.3→2.09 MB(8.3%)、`[Frankme]SexyMaid` 29.6→5.26 MB(17.8%)、
`illust_93411284` 2.3→1.11 MB(48.2%)、`104663726_p6` 2.62→1.40 MB(53.2%)、
`118563740_p3` 14.0→7.33 MB(52.4%)、`105831213_p0` 84.7%、`Luxurious Wheels V2 Alvarna` 93.1%、
`105282475_p4` 99.9%（图本来就很省）。

### 工作目录那张 `CardA.png`

65,624,725 B / **82 张贴图** / 载荷 65.2 MB / 全 PNG（71 RGBA + 11 RGB），2048² 上限：

| 方案 | 卡文件 | 比例 |
|---|---|---|
| auto q90，上限 1024 | 21,541,052 B | 32.8% |
| **auto q90 + 法线/遮罩 512** | **11,063,963 B** | **16.9%** |

两张都 `kkcard_verify.py` PASS。剩余体积的构成（1024 版）：JPEG 20 张 1.80 MB、
PNG 54 张 13.30 MB、原样透传 8 张 5.04 MB；大户全是**带 alpha 的 `NormalMap`/`NormalMapDetail`**
（`tex_107` 800²、`tex_5` 1024²、`tex_25`、`tex_46`、`tex_24`）——正是不能碰 JPEG 的那类，
所以这张卡"降到 512"比"转 JPG"更有效。

### 命令

```powershell
# 单张
python kkcard_repack.py 'in.png' 'out.png' 1024 auto 90 512
python kkcard_verify.py 'out.png'
# 盘点整个目录（只读）
python kkcard_survey.py 'E:\Koikatu-Game\UserData\coordinate'
# 批量压缩整个目录（原卡不动，逐张自动校验）
python kkcard_batch.py 'E:\Koikatu-Game\UserData\coordinate' '_work\kkcard\batch' 1024 auto 90 512
```

`kkcard_repack.py` 退出码：0 正常 / 2 不是卡 / 3 无贴图（已原样复制）/ 4 字典结构异常（未改动）。

---

## 9. 只压 MainTex（不碰法线/遮罩/高度图）

`mode=maintex` = auto 规则，但**只有绑定到 `MainTex` 的贴图会被处理**；其余贴图（`NormalMap`、
`NormalMapDetail`、`ParallaxMap`、`HighColor_Tex`、`MatCap_*`、`*Mask`、`EmissionMap`…）
**原样透传、字节不变**（不缩放、不重编码）。也支持显式白名单：第 7 个参数 `only=MainTex,Main2ndTex`。

```powershell
python kkcard_repack.py 'in.png' 'out.png' 1024 maintex 90
python kkcard_diff.py 'in.png' 'out.png'    # 逐 TexID 比对，证明没动的确实是字节相同
```

### 实测（`python kkcard_diff.py` 逐 TexID 校验）

`CardA.png`（65,624,725 B / 82 贴图）：

| 方案 | 卡文件 | 比例 | 被改动的 TexID |
|---|---|---|---|
| maintex 1024 | 50,306,215 B | 76.7% | 26 张（6,14,43,82-105 区间,108,109），56 张字节相同 |
| maintex 512 | 46,927,284 B | 71.5% | 同上 |

MainTex 那 27 张：19.44 MB → 4.84 MB（4 倍），其中 5 张 JPEG、21 张带 alpha 只能 PNG。
剩下 55 张未动 = **42.74 MB**，正是这张卡体积降不下去的原因（法线/MatCap 占大头）。

`CardA.png`（177,381,281 B / 34 贴图）：

| 方案 | 卡文件 | 比例 | 被改动的 TexID |
|---|---|---|---|
| maintex 1024 | 155,973,406 B | 87.9% | 8 张：2,9,13,16,19,23,27,36（26 张字节相同） |
| maintex 512 | 154,546,254 B | 87.1% | 同上 |

MainTex 8 张：22.27 MB → 1.85 MB（12 倍）；**没动的 26 张 = 146.35 MB（占 83%）**——
所以这张卡只压 MainTex 收益很小，收益上限被"保留法线贴图"这个前提锁死了。

### 边界情况

- `tex_40`（coord 卡）：本身就是 1024² 且很小 → 重编码不更小，走 `orig` 原样透传。
- `tex_105`：alpha 范围 230..255（几乎不透明），但只要有 1 个像素 <255 就按"有 alpha"处理 → PNG，
  427,992 → 415,629 B 只小 3%。若接受把这种"几乎全不透明"的 alpha 抹平，可以加阈值（如 alpha min ≥200
  就允许 JPEG），但这会改动镂空边缘，默认不做。
- 未绑定任何 property 的贴图（卡里有孤立 blob 时）不会被 `maintex` 命中 → 也不动。

---

## 10. 图形界面（`kkcard_gui.py`）

启动：**双击 `运行压缩工具.bat`**（同目录），或命令行 `python kkcard_gui.py`。

### 界面能设什么

1. **输入**：单个卡片文件，或整个文件夹（批量）。
2. **输出目录 + 文件名后缀**（默认 `" (压缩)"`），可选是否覆盖同名输出；**原卡永不被改动**。
3. **每种贴图类型一行，单独设「处理方式」和「最大边」**——类型是从卡内
   `MaterialTexturePropertyList` 的 shader 属性名判定的，不写死 TexID：

   | 类型 | 判定依据 | 默认 |
   |---|---|---|
   | 主贴图 MainTex | `*maintex*` | 自动 / 1024 |
   | 法线 Normal/Bump | `*normal*`、`*bump*` | 原样 |
   | 遮罩 Mask | `*mask*` | 原样 |
   | 高度/视差 | `*parallax*`、`*height*` | 原样 |
   | 发光 Emission | `*emission*`、`*emitt*` | 原样 |
   | 反射 Reflection | `*reflection*`、`*reflective*` | 原样 |
   | 材质球 MatCap | `*matcap*` | 原样 |
   | 高光色 HighColor | `*highcolor*`、`*high_color*` | 原样 |
   | 其它/未分类 | 以上都不匹配 | 原样 |

   处理方式四选一：`原样不动`（字节透传）/ `自动`（安全且更小才用 JPEG）/ `全部 JPEG` / `全部 PNG`。
   最大边：原尺寸 / 4096 / 2048 / 1024 / 512 / 256。
4. **全局**：JPEG 质量滑杆（50~100，默认 90）、`保护 alpha`（含透明的贴图即使选 JPEG 也存 PNG，默认开）。
5. **两个预设**：`只压主贴图（已验证）` = 默认计划；`主贴图 1024 + 其它 512`（尺寸减半但不动格式编码）。
6. **按钮**：①扫描（显示每种类型有几张/多少 MB/最大边长）→ ②开始压缩 → 取消 / 打开输出目录 /
   另存为计划 JSON（可给命令行批处理复用）。

### 安全设计

- 每张卡压完立刻做结构校验（PNG/IEND + 字典可解析 + 每张贴图能解码），失败会标 `[X] 校验失败`。
- 单张贴图**只要重编码不更小就原样透传**，所以任何卡都不会变大（实测 `Luxurious Wheels V2 Alvarna` 100.0%）。
- 取消是安全的：中途取消不会写出半成品文件。
- 没有贴图的普通卡自动跳过并计入「跳过（无贴图）」。

### 自检（`python -u _gui_selftest.py`，真实 Tk 控件跑一遍）

```
A 扫描完成 / maintex 计划 = auto/1024 / normal 保持不动         OK
B 计划 reflection=png/256 → 产出 3 张贴图全部 PNG 256px         OK
C 批量文件夹 2 张卡压缩 + 校验                                  OK
D 中途取消 → 已取消，未写出文件                                  OK
===== GUI 自检 全部通过 =====
```

### 命令行等价物

界面设置可以「另存为计划 JSON」，再用批处理复用（`plan=` 参数覆盖 mode/尺寸参数）：

```powershell
python kkcard_batch.py <输入目录> <输出目录> 1024 plan=plan.json
python kkcard_repack.py 'in.png' 'out.png' 1024 maintex 90      # 只压主贴图
python kkcard_repack.py 'in.png' 'out.png' 1024 auto 90 512     # 法线/遮罩上限 512
```

`plan.json` 格式：`{"maintex": {"format": "auto", "size": 1024}, "normal": {"format": "keep", "size": 0}, ...}`，
`format` ∈ `keep|png|jpeg|auto`，`size=0` 表示不缩放。

---

## 11. 原生 EXE 版（`KoiCardTexTool.exe`，不需要 Python）

本机没有 PyInstaller / Nuitka / cx_Freeze，且网络完全不通，**装不了打包器**。
改用 .NET 8 自带的 Windows Desktop（已装 SDK 8.0.425 + `Microsoft.WindowsDesktop.App.Ref` 8.0.31），
把同一套算法**用 C# 重写**成 WinForms 程序，离线编译成原生 EXE。

### 位置与用法

- 成品：`KoiCardTexTool\KoiCardTexTool.exe`（单文件 196,362 B）+ `使用说明.txt` + `plan_maintex.json`
- 源码：`_work\kkcard_exe\`（`KoiCardTexTool.csproj` + `src/{Msgpack,Card,TexTool,MainForm,Program}.cs`）
- 重新编译：
  ```powershell
  cd _work\kkcard_exe
  $env:APPDATA='.<...>\.appdata'; $env:DOTNET_CLI_HOME='.<...>\.dotnet'; $env:NUGET_PACKAGES='.<...>\.nuget'
  dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true `
                 -p:PublishTrimmed=false -p:_RequiresILLinkPack=false -o dist
  ```
  坑：`PublishSingleFile=true` 会要求 `Microsoft.NET.ILLink.Tasks` 包（离线取不到），
  必须加 `-p:_RequiresILLinkPack=false` 才能出单文件；`NuGet.config` 里 `<clear/>` 掉源避免联网等待。

### EXE 与 Python 版的一致性（已实测）

| 项 | 结果 |
|---|---|
| 分类/扫描 | 完全一致（同一张卡：34 张、maintex 8/22.27MB、normal 9/129.76MB、mask 13/12.01MB、matcap 4/4.58MB） |
| `maintex` 模式动过的 TexID | EXE 与 Python **都是 2,9,13,16,19,23,27,36**，其余 26 张 sha256 完全相同 |
| 输出可被 Python 工具读 | `kkcard_verify.py` 对 EXE 产物 → **PASS**（34 张贴图全部可解码） |
| 贴图分辨率/格式选择 | 34 张全部一致（JPEG 11 / PNG 23 的划分也一样） |
| 体积 | EXE 更大：贴图载荷 13.71 MB vs Python 12.48 MB（1.10×）——GDI+ 的 PNG/JPEG 编码器不如 Pillow+optimize 紧，仅 3 张（`tex_34` RGBA、`tex_17`/`tex_37` 调色板）明显偏大 |
| 整卡 | White&Deep_Blue auto1024：EXE 14,951,425 B vs Python 13,662,388 B；KKCoordeF maintex1024：49,933,282 vs 50,306,215（这里 EXE 反而略小） |

### C# 实现要点（与 Python 版的对应关系）

- `Card.PngEnd()`：按 chunk 长度走 IEND（同 Python `png_end`）。
- `Card.FindKey()`：按 fixstr/str8/16/32 长度前缀找 msgpack 键（同 Python `find_key`）。
- `MP.Read()`：完整 msgpack 读（map/array/bin/str/各种整数），`MP.MapHeader/Int/Bin32` 写回；
  map 头同样用**最小形式**（≤15 用 fixmap），否则小字典卡会平白涨 2 字节。
- 贴图键非整数时直接拒绝写入（rc=4），不会像 Python 版那样写 0 造成损坏。
- 图像：`System.Drawing`（GDI+）。缩放 `HighQualityBicubic` + `PixelOffsetMode.HighQuality`；
  贴图若"全灰且无 alpha"打包成 8bpp 灰度 PNG（相当于 Python 的调色板量化），
  无 alpha 用 24bpp、有 alpha 用 32bpp。
  **坑**：GDI+ 不能对索引位图 `Graphics.FromImage` → 必须先画到 24/32bpp 再用 `LockBits` 打包成 8bpp。
- 同样保留"重编码不更小就原样透传"的守卫，所以 EXE 版也不会让卡片变大。
- 单文件 EXE 与 GUI 同体：无参数启动界面，带参数（`scan`/`compress`/`batch`）走命令行，
  并用 `AttachConsole(ATTACH_PARENT_PROCESS)` 把输出接回终端；另支持 `log=out.txt` 落盘。

---

## 12. 拖入即读（EXE 版）+ 本次改动

### 三种拖法（都已实测）

| 拖法 | 行为 |
|---|---|
| 把 .png 拖到 **EXE 图标** 上 | `Main()` 发现首个参数不是子命令 → `new MainForm(args)`，`OnShown` 里 `AcceptPaths()` 直接读卡 |
| 把 文件夹 拖到 EXE 图标上 | 同上，走批量模式 |
| 把文件拖进 **窗口** | `AllowDrop` + `DragEnter/DragDrop`（提示条、路径框、日志区都挂了 handler）；1 张→单卡，多张→`dropped` 列表批量，1 个文件夹→批量 |

拖入后立刻扫描并把卡信息写进日志（`TexTool.Describe` + `Card.ReadInfo`）：
```
CardA.png  〖KoiKatuClothes〗 版本 0.0.0 / 数据 0.0.2  名称：White&Deep_Blue 2  部件 9 个
  贴图 34 张 / 168.62 MB
    maintex       8 张    22.27 MB  最大 2048px  ...
```
新增 `info` 命令（同一份 Describe）：`KoiCardTexTool.exe info "卡片.png"`。
新增隐藏自检 `uitest <路径...> log=out.txt`：建隐藏窗口 → 调 `AcceptPaths` → 泵消息等扫描完成 →
打印 STATUS/INPUT/OUTDIR/DROPBAR/控件矩形/PLAN/日志。**用它替代人工点击验证拖入逻辑**，实测：
单卡、文件夹、多张（`INPUT=（已拖入 2 张卡：…）`）三种都正确，且 `drop={0,0,1000,30}` 与
`content={0,30,1000,760}` 不重叠（Dock=Top 提示条布局正确）。

### 卡信息读取（`Card.ReadInfo`）

头部是 `int32 LE(100) | 1 字节长度+UTF-8 字符串 ×3（tag/版本/卡名）| 8 字节 | msgpack map`。
坑：第一份 msgpack 文档的键 `version` 前面是 **map 头**（实测 `0x86`），
按 `0xa7version` 搜到的是**键**的位置，直接 `Read` 只会得到字符串 "version" →
必须回退到 map 头再读；map16/map32 要分别回退 3/5 字节。修好后：
`CardA.png` → 数据 0.0.1 / 部件 9 个（卡名本身是空的，Python 侧同样读到空）。

### 分类优先级调整（C# 与 Python 同步改）

`遮罩 > 法线 > 高度 > 主贴图 > 发光 > 反射 > 材质球 > 高光色 > 其它`
（原来 `主贴图` 排在最后）。原因：`HighColor_Tex,MainTex` 这类贴图原本被算作"高光色"，
于是界面点「只压主贴图」时它不会被处理，而 CLI 的 `maintex` 模式（按属性名白名单）却会压它——
两边口径不一致。改后 `KKCoordeF` 的 maintex 从 26 张/19.15MB 变成 **27 张/19.44MB**，
与 CLI 的 `maintex` 模式完全一致；`kkcard_policy.py` 的 `_RULES` 也同步调整。

### 两个真 bug（EXE 版新踩）

1. **`BeginInvoke` 不能在构造函数里调用**（窗口句柄还没建）→ 带路径启动 EXE 时
   进程直接以 `0xE0434352`（未处理异常）退出。改成在 `OnShown` 里触发。
2. **环境变量 APPDATA 重定向会让 Python 找不到 Pillow**：本机 Pillow 是装到
   `%APPDATA%\Python\Python313\site-packages` 的 per-user 安装，构建 EXE 时为了不写
   `C:\Users\...` 把 `APPDATA` 指向工作区，同一会话里再跑 `kkcard_survey.py` 就报
   `No module named 'PIL'`。教训：构建用的环境变量只在构建那一条命令里设。

---

## 13. 自包含版（不依赖 .NET 运行时）

网络放通后（注意：**要 `danger-full-access`，默认 workspace-write 下 shell 完全没有出网**——
实测连 baidu.com 的 HTTPS 都失败、`Test-NetConnection` 443 却是 True、`dotnet restore` 报 NU1301；
放宽后 `curl https://api.nuget.org/v3/index.json` 立刻 200），把 `NuGet.config` 的
`<clear/>` 改回 nuget.org，然后：

```powershell
$env:APPDATA=...\kkcard_exe\.appdata; $env:DOTNET_CLI_HOME=...\.dotnet; $env:NUGET_PACKAGES=...\.nuget
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:PublishTrimmed=false `
  -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:_RequiresILLinkPack=false -o dist_sc1
```

产物：**单文件 `KoiCardTexTool.exe` 65,863,854 B（65.9 MB）**，无任何伴随文件。
- `--self-contained true` 会从 nuget.org 下载 `Microsoft.NETCore.App.Runtime.win-x64` +
  `Microsoft.WindowsDesktop.App.Runtime.win-x64` 运行时包；`NUGET_PACKAGES` 指向工作区，不污染 C:\Users。
- `EnableCompressionInSingleFile=true`：内部 Brotli 压缩，体积从 ~150 MB 降到 66 MB。
- `IncludeNativeLibrariesForSelfExtract=true`：把 D3DCompiler_47_cor3.dll / PenImc / PresentationNative /
  vcruntime140_cor3 / wpfgfx 这些原生 DLL 也塞进单文件（否则会散落在旁边）。
- 没有做 trimming（WinForms 官方不支持裁剪，容易运行时炸）。

### 验证

| 检查 | 结果 |
|---|---|
| 拷到别的目录、清空 DOTNET 环境变量后运行 | 正常，`info` 0.7 秒返回（`DOTNET_ROOT=C:\no-such-dotnet`、`DOTNET_MULTILEVEL_LOOKUP=0`） |
| EXE 内是否打包了运行时 | 66 MB 版含 `System.Private.CoreLib` / `System.Windows.Forms.dll`；199 KB 精简版一个都没有 |
| 功能回归 | `maintex` 压缩 177,381,281 → 156,163,233 B（与精简版**同一尺寸**），改动 TexID 仍是 2,9,13,16,19,23,27,36，其余 26 张 sha256 相同；Python `kkcard_verify.py` PASS |
| 拖入 / 批量 / GUI | `uitest` 拖入扫描正常；`batch` + `plan.json` 2/2 PASS；GUI 双击启动、带卡启动都正常 |

### 交付

`KoiCardTexTool\`
- `KoiCardTexTool.exe` 65.9 MB —— 便携版（免 .NET/免 Python，可拷到别的电脑）
- `KoiCardTexTool-lite.exe` 199 KB —— 精简版（需 .NET 8 桌面运行时）
- `plan_maintex.json`、`使用说明.txt`（已写明两个版本的区别，推荐发便携版）

---

## 14. 修掉的崩溃：CultureNotFoundException（invariant globalization vs WinForms）

### 用户报错

```
System.Globalization.CultureNotFoundException:
Only the invariant culture is supported in globalization-invariant mode.
1033 (0x0409) is an invalid culture identifier.
```

（`OutputType=WinExe` 没有控制台，所以这是 WinForms 崩溃对话框的文本，表现为"报错退出"。）

### 根因

`_work\kkcard_exe\KoiCardTexTool.csproj` 里早先为缩体积写了 `<InvariantGlobalization>true</InvariantGlobalization>`。
该开关把运行时切到 **globalization-invariant 模式**：`CultureInfo.CurrentCulture.Name` 变空串，任何 `new CultureInfo(具体区域)` 一律抛异常。
**WinForms 不支持 invariant 模式**（框架内部会取具体区域，在英文 Windows 上就是 en-US=1033）。

### 判定证据（同一份源码，只切开关）

```
dotnet build -c Release -o dist_cfg -p:InvariantGlobalization=true   # BAD
dotnet build -c Release -o dist_ok                                   # GOOD（csproj 已改 false）
```

| 探针 | BAD(Invariant=true) | GOOD(Invariant=false) |
|---|---|---|
| `dlgtest` 打印区域 | `culture= ui= invariant-mode=(name 为空)` | `culture=zh-CN ui=zh-CN invariant-mode=no` |
| `[1..4]` 构造 OpenFileDialog/SaveFileDialog/FolderBrowserDialog/MainForm | 通过 | 通过 |
| `[6..9]` 消息循环里 `MessageBox.Show(YesNo)` / `OpenFileDialog.ShowDialog` / `FolderBrowserDialog.ShowDialog` | 通过（本机未自然触发） | 通过 |
| `[5] new CultureInfo(1033)` | **抛出用户那条原话** | `English (United States)` |
| `uitest <card> run=1`（GUI 压缩全链路） | 通过 | 通过 |

本机 OS UI 语言 = **zh-CN (0804)**（`InstalledUICulture` / `HKCU PreferredUILanguages` / `HKLM Nls\Language` 三处一致），
所以错误里的 **en-US(1033) 不可能来自本机系统语言** → 极可能是把便携版拷到**英文 Windows** 上运行触发的。

结论：不能因为"本机复现不出来"就不改。invariant 模式 + WinForms 本身就是不受支持的组合，直接关掉，整类失败模式消失。

### 修复

1. `KoiCardTexTool.csproj`：`<InvariantGlobalization>false</InvariantGlobalization>`（带注释说明为什么不能打开）。
2. `src/Program.cs` 新增全局崩溃兜底 `InstallCrashHandlers()`：
   - `Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException)`
   - `Application.ThreadException` → `Report("[界面线程异常]", ex)`
   - `AppDomain.CurrentDomain.UnhandledException` → `Report("[后台线程异常]", ex)`
   - `Application.Run(...)` 也包 try/catch → `Report("[启动异常]", ex)`，返回码 3
   - `Report()` 把 `ex.ToString()` + EXE 路径 + OS 版本 + CLR 版本 + 当前区域写进 **EXE 同目录** `KoiCardTexTool-crash.log`（写不进去退回 `%TEMP%`），再弹窗提示；`CrashLogPath()` 公开。
3. 新增无头探针子命令（顺手把 GUI 压缩路径纳入自检）：
   - `KoiCardTexTool.exe dlgtest [full]` —— 打印区域 + 构造对话框/窗体；`full` 借 `AutoCloseDialog(1500)`（`EnumWindows` 找本进程 `#32770` 窗口发 `WM_CLOSE`）自动关模态框，实测 `MessageBox.Show` 与两个 `ShowDialog`。
   - `KoiCardTexTool.exe mboxtest` —— 在真的 `Application.Run` 消息循环里弹 `MessageBoxButtons.YesNo`，覆盖"模态框 + 消息循环"这条与 CLI 不同的路径。
   - `uitest <path...> run=1` —— 拖入扫描后走界面「开始压缩」的 `DoRun`（新增 `MainForm.UiRun()`），等 `完成：`/`已取消`/`运行出错` 后打印 `RUNSTAT`。

### 修复后验证

| 检查 | 结果 |
|---|---|
| `dlgtest full` | 两版都 `culture=zh-CN / invariant-mode=no`，`new CultureInfo(1033)` 正常，`[9] 对话框实测通过` |
| `mboxtest` | 两版都通过（返回 Yes） |
| `uitest KKCoordeF…png run=1` | 两版一致：`完成：成功 1 张…62.58 MB -> 47.62 MB（76.1%）`，`[OK] 校验通过：82 张贴图可解码` |
| 主卡回归 `compress … maintex` | **177,381,281 → 156,163,233 B**（与修复前同尺寸），`[OK] 校验通过: 34 张贴图可解码` |
| 双击启动 | 两版 GUI 均存活、无 `KoiCardTexTool-crash.log` |
| 产物 | `KoiCardTexTool.exe` 65,866,562 B；`KoiCardTexTool-lite.exe` 208,591 B |

### 坑

- **别给 WinForms 程序开 `InvariantGlobalization`**；`SatelliteResourceLanguages=en` 本身无害。
- 离线重发自包含版：运行时包已在 `_work\kkcard_exe\.nuget`，把 `NUGET_PACKAGES` 指过去就**不需要联网**（指到空目录会 `NU1301 无法加载源 https://api.nuget.org/v3/index.json`，沙箱里 shell 无出网）。
- `dotnet publish -p:InvariantGlobalization=true` 的命令行属性**确实覆盖 csproj**（查 `KoiCardTexTool.runtimeconfig.json` 的 `System.Globalization.Invariant` 可证）——A/B 对照就靠这点。
- PowerShell 5.1 里 `if` 不是表达式：`"x" + (if(...){...}else{...})` 报 `The term 'if' is not recognized`，要用 `$(if(...){...}else{...})`。

---

## 15. 修掉的崩溃 2：`Parameter is not valid.`（卡里有 Radiance HDR）

### 现象

用户压缩 `CardA.png`（45,780,713 B）失败，界面日志一行：

```
[X] 失败: Parameter is not valid.
```

CLI 复现（exit=2）：

```
[X] System.ArgumentException: Parameter is not valid.
   at System.Drawing.Image.LoadGdipImageFromStream(GPStream stream, Boolean useEmbeddedColorManagement)
   at System.Drawing.Image.FromStream(Stream stream, Boolean useEmbeddedColorManagement, Boolean validateImageData)
   at KoiCardTexTool.TexTool.Load(Byte[] raw)
   at KoiCardTexTool.TexTool.Repack(...)
```

`Parameter is not valid.` 是 **GDI+ 加载失败**的经典文案。

### 定位

抽出卡里 33 个贴图 blob，逐个看签名：32 个是 PNG（`89504E47`），
**`tex_6` 是 21,211,844 B 的非图像**，头 80 字节：

```
23 3F 52 41 44 49 41 4E 43 45 0A ... "#?RADIANCE\n# Created with Blender\nEXPOSURE= 1.0\nFORMAT=32-bit_rle_rgbe"
-Y 2160 +X 4096
```

即 **Radiance HDR（RGBE）**，材质绑定 `DreamPool_MainSleeve / ReflectionCubeTex / TexID 6`
（lilToon 的环境反射图），占整张卡 **46%**。GDI+ 解不了 HDR → `Image.FromStream` 抛异常。

另有一个 `tex_33`（32,405 B）其实是 **JPEG**（`FFD8FFE1`），只是我的提取脚本按 PNG 签名判断才命名成 `.bin`。

三个缺陷一起暴露：①对每张贴图**无条件**调 GDI+ 解码；②一张失败**整张卡作废**；
③`QuickVerify` 也硬要求 PNG/JPEG，修复后仍会把成品判失败。

### 修复

1. **签名识别**（`TexTool.Sniff` → `TexFmt.Png/Jpeg/Hdr/Exr/Unknown`；`FmtName`；`DimOf` 对 HDR 读分辨率行取尺寸）。
2. **单张隔离**：`Repack` 里每张贴图包 try/catch —— 解不开就**原样保留这一张**并打
   `[跳过] TexID n (格式, 字节): 消息 —— 这一张原样保留，其余继续`；卡其余部分照常压。
   非 PNG/JPEG 的未知格式直接透传，根本不进 GDI+。
3. **自研 HDR 编解码**（新文件 `src/Hdr.cs`）：`Decode` / `Encode` / `ResizeBox` / `PeekSize` / `IsHdr` / `ReencodeSame`。
   - 用 Radiance 官方 `setcolr`/`colr_color` 配对公式（`value = byte * 2^(E-136)`、`scale = m*256/v`、`E = e+128`，COLXS=128）→ **解码后按原尺寸重编码逐位还原**（不用 `+0.5` 那个写法，那个有 1 LSB 误差）。
   - 新式 RLE（`2 2 hi lo` + 4 通道游程）与老式平铺两种都支持；编码端字面量游程最多 128、重复游程 ≥4 才用。
   - 缩放必须**在线性空间**做盒式平均（HDR 的"平均值"就是线性平均）。
   - 规则：`反射` 类为 keep → 完全不动；给了上限 → 缩放后**仍是 HDR（RGBE）**，绝不会退化成 JPEG/PNG；编出来更大就原样保留。
4. `QuickVerify` 重写：PNG/JPEG 走 GDI+，HDR 用 `Hdr.Decode` 整张解一遍，其它格式只做存在性检查；
   顺带修掉原来用 `dic.Keys[n]`（n 只数 BinRef）导致 TexID 报错的**下标错位**（改成按 `i` 索引）。
5. 日志新增 `HDR` 计数：`贴图 33 张：JPEG 11 / PNG 20 / HDR 1 / 原样 0（q90）`。
6. **Python 侧同步**（否则 `kkcard_verify.py` 会把成品判 FAIL）：
   `kkcard_msg.py` 新增 `sniff(raw)` / `hdr_size(raw)`；`kkcard_verify.py`、`kkcard_policy.py`（scan + quick_verify）、
   `kkcard_survey.py` 改用它们；`kkcard_repack.py` 对非 PNG/JPEG 走**原样透传**分支
   （Python 不解码 HDR，日志明说"缩它请用 EXE 版"）。

### HDR codec 验证（双实现交叉验证）

| 检查 | 结果 |
|---|---|
| C# 自洽：`hdrtest` 解码→同尺寸重编码→再解码 | **逐位相同（差异 0 个分量）**；21,297,901 B vs 原 21,211,844 B（RLE 效率与 Blender 同级，差 0.4%） |
| C# 解码统计 | 4096x2160 `sum=9979934.8187 mean=0.376004 min=0.000054 max=18.5000` |
| 独立 Python 解码器 `_work\tex\hdr_cross.py`（按规范重写，非 C# 移植）解同一文件 | `sum=9979934.8187 mean=0.376004 min=0.000054 max=18.5000`，**consumed 21211844/21211844**（RLE 解析刚好吃完，无漂移） |
| 缩放 1024x540 后 Python 复解 | `1,484,924 B`、`sum=622427.7341`（与 C# 完全一致）、`mean=0.375210`（盒式平均保持均值）、`max=18.25`（最亮像素被平均，符合预期） |
| Python `kkcard_verify.py` 判 EXE 产物 | auto 产物 `RESULT: PASS`；maintex 产物 `RESULT: PASS`（HDR 行显示 `HDR 4096x2160 RGBE`） |

### 端到端结果

| 卡 | 模式 | 结果 |
|---|---|---|
| `CardA.png`（33 张，含 1 张 HDR） | auto 1024 q90 | 45,780,713 → **8,573,170 B（18.7%）**；HDR 21,211,844 → 1,484,924（-93%） |
| 同上 | maintex 1024（HDR 不动） | 45,780,713 → 38,219,502 B（83.5%），`HDR 0 / 原样 26` |
| 同上，走 GUI `uitest … run=1`（用户报错的路径） | 默认=只压主贴图 | `完成：成功 1 张 … 失败 0 张`，`[OK] 校验通过（HDR 1 张按 RGBE 整张解码校验）` |
| `CardA.png`（169.8 MB，**8192px** 主贴图） | auto 1024 q90 | 169,859,035 → **9,814,143 B（5.8%）**，8.2 秒 |
| **回归**：9 张样本卡 batch（改动前 EXE vs 改动后 EXE） | auto 1024 q90 | 两次总量都是 `277896015 -> 41373883（14.9%）`，**10/10 文件 SHA256 逐字节相同** |

产物：`KoiCardTexTool.exe` 65,870,777 B（自包含）、`KoiCardTexTool-lite.exe` 217,295 B。

### 坑

- `Image.FromStream` 的 `Parameter is not valid.` 只有一个原因：**GDI+ 不认识这个格式/数据**。
  卡片贴图**不能假定是 PNG**；先按签名分流，再决定要不要交给 GDI+。
- 一遇到怪贴图就 `throw` 会把整张卡作废；改成"单张失败→原样保留→继续"后，用户体验完全不同。
- 校验函数必须与产出函数用**同一套格式判断**，否则会出现"压出来了但自检 FAIL"的假警报。
- Python 侧 `kkcard_survey.py` 对 169.8 MB 那张卡仍报"无 MaterialEditor 贴图"（同卡的
  `kkcard_verify.py` 能正确看到 15 张）——survey 的键检测口径与 verify 不一致，属未修的次要问题。

## 16. 失败汇总：跑完告诉你「哪张卡没成功、为什么」

用户原话：「添加功能：将压缩失败的文件名称以及原因输出到日志最后的汇总中」。

### 改动

| 文件 | 改动 |
|---|---|
| `_work\kkcard_exe\src\TexTool.cs` | `RepackResult` 新增 `public string Reason = ""`（Rc≠0 时的一句话原因）与 `public readonly List<string> Skipped`（贴图级跳过：`"TexID 6 (HDR): …"`）；新增静态 `RepackResult.RcText(int rc)` 把返回码翻成人话；新增 `TexTool.PrintSummary(Action<string> log, int nOk, int nSkip, int nFail, long bytesBefore, long bytesAfter, List<string> skipped, List<string> failed, List<string> partial)` —— CLI 与 GUI 共用同一段措辞 |
| 各 `Rc` 返回点 | `Rc=2/3/4/5` 全部同时写 `Reason`（`不是服装卡（没有 PNG/IEND 结构）` / `卡里没有贴图（没有 MaterialEditor TextureDictionary）` / `TextureDictionary 不是 bin` / `TextureDictionary 不是 map` / `贴图键不是整数（x）` / `已取消`） |
| 贴图级跳过处 | HDR「不缩小/编码后更大」、未知格式透传、GDI+ 解码失败，三处都往 `res.Skipped` 记一条带原因的记录 |
| `src/Program.cs` 的 `batch` | 逐文件收集 `failList`/`skipList`/`partList`；`Repack` 外层 try/catch 把"读取/处理时异常"也塞进失败清单；行内输出从 `失败 rc=1` 改成 `失败 —— 原因`；结束打印 `PrintSummary`；**有失败时退出码 1**（全成功 0） |
| `src/Program.cs` 的 `compress` | 失败时打 `[X] 失败 —— 原因`，并打印同一份汇总 |
| `src/MainForm.cs` 的 `DoRun` | 线程内维护同一份三个清单，`Rc=3` 记跳过原因、`Rc≠0` 记 `Reason`、校验失败记 `产出校验失败: msg`、异常记 `读取/处理时异常: msg`；收尾调用 `PrintSummary` |

### 输出长相（同一段代码，两边一致）

```
================ 汇总 ================
成功 2 张，跳过 2 张，失败 2 张
成功部分的体积：705172 -> 692195 字节（98.2%）
失败清单（2 张）：
  [X] brokenstruct.png —— TextureDictionary 不是 bin
  [X] notacard.png —— 不是服装卡（没有 PNG/IEND 结构）
跳过清单（2 张，卡里本来就没有可压的贴图，不算失败）：
  [跳过] plainimage.png —— 卡里没有贴图（没有 MaterialEditor TextureDictionary）
贴图级跳过（1 处：这些贴图原样保留，所属卡片本身压缩成功）：
  [跳过] badtex.png: TexID 1 (未知格式): 本工具不认识这种格式，原样保留
=====================================
```

三个清单的语义（这是本功能的重点，别混）：
- **失败**：产出不可用（原图不是卡 / 结构不认识 / 写出后校验不过 / 读写异常）。
- **跳过**：卡里没有可压的贴图，工具把原文件原样复制，**不是坏事**。
- **贴图级跳过**：卡压成功了，但其中几张贴图没动，原因跟在后面（HDR 已在下限内、未知格式、单张解码失败）。

### 验证（每条分支都真跑到了）

夹具 `_work\tex\make_bad_cards.py` → `_work\kkcard\failing\`：

| 夹具 | 构造方式 | 期望 | 实测 |
|---|---|---|---|
| `notacard.png` | 4096 字节随机数（.png 扩展名） | Rc=2 失败 | `[X] notacard.png —— 不是服装卡（没有 PNG/IEND 结构）` ✓ |
| `nocard.png` | 把 `TextureDictionary` 键的长度前缀 `0xB1`→`0xB0`（17→16 字符，键匹配不上） | Rc=3 跳过 | `[跳过] nocard.png —— 卡里没有贴图…` ✓ |
| `brokenstruct.png` | 键后面的值标记 `0xC6`(bin32)→`0xC0`(nil) | Rc=4 失败 | `[X] brokenstruct.png —— TextureDictionary 不是 bin` ✓ |
| `badtex.png` | 第一张贴图 blob 首字节置 0（PNG 签名毁掉） | Rc=0 + 贴图级跳过 | `[跳过] badtex.png: TexID 1 (未知格式): 本工具不认识这种格式，原样保留` ✓ |
| `plainimage.png` | Pillow 生成的普通 PNG（合法 PNG 但不是卡） | Rc=3 跳过 | ✓ |
| `ok.png` | 未改动的真实卡（对照） | Rc=0 | 96.3%，PASS ✓ |

- CLI：`batch` 汇总四条分支全命中，退出码 1（有失败）；
- GUI：`uitest <failing 目录> run=1` 的日志里出现**同一段**汇总（逐行一致）；
- `compress` 单文件失败：`[X] 失败 —— 不是服装卡（没有 PNG/IEND 结构）` + 汇总，退出码 2；
- **回归**：新 EXE vs 部署中的上一版跑同 9 张样本卡 → `277896015 -> 41373883（14.9%）`，
  **10/10 SHA256 逐字节相同**（汇总功能不碰压缩路径）。

### 坑

- `"TextureDictionary"` 是 **17** 个字符（fixstr 前缀 `0xB1`）——我一开始按 18 数错，
  夹具脚本报 `expected fixstr(18) prefix, got 0xB1` 才发现。写 msgpack 夹具时先 `len()` 一下。
- 用 `bytes.find(b'TextureDictionary')` 定位键会命中**长字符串中间**的那一段，
  必须用 `kkcard_msg.find_key()`（它校验长度前缀 `(pfx & 0x1f) == len(name)`）。
- **PowerShell 不等待 GUI 子系统（WinExe）进程**：`& exe args` 立即返回，紧接着去看输出/文件
  必然"什么都没有"。要看结果用 `2>&1 | Out-String`、`Start-Process -PassThru` + `WaitForExit()`，
  或者让程序自己 `log=文件`。本会话因此两次误判"CLI 不打印"。
- WinExe 没有自己的控制台：`AttachConsole(ATTACH_PARENT_PROCESS)` 之后 stdout 句柄**未必有效**，
  这时 `Console.OpenStandardOutput()` 返回的是 `Stream.Null`；**千万别拿它去 `Console.SetOut`**，
  否则之后所有 `Console.WriteLine` 被静默吞掉。`HookConsole()` 里加 `so != Stream.Null` 守卫。
- 重定向 `$env:APPDATA` 给 dotnet 用之后**别再在同一个命令里跑 Python**：本机 Pillow 装在
  `%APPDATA%` 下的用户级 site-packages，会直接 `ModuleNotFoundError: No module named 'PIL'`。

## 17. 整个文件夹递归 + 输出摆回原结构（`<输入文件夹>\<文件夹名>[zip]\`）

用户原话：「加入一个对一个文件目录下的所有子文件夹进行读取的功能，输出的文件以相同的结构放原文件夹+[zip]文件夹中。同时默认文件名文件后缀改为[zip]，输入文件夹时将文件放在文件夹内的[文件夹名称+[zip]文件夹中]。」
（两处读法有歧义，已确认：输出放**输入文件夹内部**的「文件夹名+[zip]」子文件夹；界面「文件名后缀」框默认值 = `[zip]`。）

### 行为

```
输入 D:\cards            →  输出 D:\cards\cards[zip]\
     D:\cards\A\x.png     →       D:\cards\cards[zip]\A\x[zip].png
     D:\cards\B\c\y.png   →       D:\cards\cards[zip]\B\c\y[zip].png
```

- 递归默认开启；GUI 用勾选框「包含子文件夹（递归，输出保持同样结构）」（默认勾上），CLI 用 `recurse=0` 关掉。
- 输出卡名默认「原名[zip].png」；GUI 是「文件名后缀」框（默认值由 `" (压缩)"` 改成 `"[zip]"`），
  CLI 是 `suffix=`（`suffix=` 空 = 保持原名，`suffix=_small` = 换别的）。
- `batch` 的 `out_dir` 变成**可省**参数，省略就 `TexTool.DefaultOut(inDir)`；参数解析改成「选项随处可写」，
  位置参数依次是 `out_dir(可省) max_size mode quality mask_size`，第一项若是纯数字就按 `max_size` 认。
- `scan` 也支持目录（递归列出，末尾打合计贴图数）。

### 关键设计：怎样不把自己的产出再压一遍

默认输出目录就在输入文件夹**里面**，递归必然撞上自己，于是两道排除：

1. 目录名以 `[zip]` 结尾 → 跳过（用户指定的命名约定）。
2. 目录里存在标记文件 `.koicardtex-outdir`（`TexTool.OutMarker`，`TexTool.MarkOut(dir)` 写入）→ 跳过。

第 2 条是实测逼出来的：先只做了第 1 条，结果**显式指定的自定义输出目录**（`out_plain`）不含 `[zip]`，
第二次递归跑就被当输入，一次多出 1 张、体积还从 24.9% 变 32.5%（把上次的产物又压了一遍）。
光靠名字猜不可靠，所以改成留标记文件。输出目录仍会排除 `outDir` 子树本身（`Enumerate(root, outDir, recurse)`）。

### 新增/改动代码

| 位置 | 内容 |
|---|---|
| `_work\kkcard_exe\src\TexTool.cs` | `public class CardFile { public string Src; public string Rel; }`（`Rel` = 相对输入根的子目录，`""` = 顶层）；`public const string OutMarker = ".koicardtex-outdir"`；`static void MarkOut(string dir)`；`List<CardFile> Enumerate(string root, string outDir, bool recurse)` + `static void WalkDir(string rootFull, string dir, string exclude, bool recurse, List<CardFile> list)`（手写递归以便剪枝，`Directory.GetFiles(..., AllDirectories)` 剪不了）；`static string DefaultOut(string inputPath)`（目录 → `输入\名字[zip]`，文件 → `父目录\压缩输出`） |
| `src/Program.cs` `batch` | 选项随处解析（`plan=` / `recurse=` / `suffix=`）；`out_dir` 可省；按 `cf.Rel` 建子目录并写 `<stem><suffix>.png`；行内与汇总里的文件名显示成相对路径 `A\a1\x.png`；结尾打 `输出目录: ...`；`static bool IsAllDigits(string)` |
| `src/Program.cs` `scan` | 参数可为目录：用 `Enumerate` 递归列相对路径，末尾合计 |
| `src/MainForm.cs` | 新勾选框 `chkRecurse`（默认勾上，放在「文件名后缀」那一行的流式面板里，布局自检 RECT 不变）；`txtSuffix.Text = "[zip]"`（宽度 140→100）；`Inputs(out string outDir)` 改为返回 `List<TexTool.CardFile>`（先定输出目录再枚举，否则排除不了）；`static string Rel(TexTool.CardFile cf)`；`DoRun` 按 `Rel` 建子目录、后缀命名、日志与三个汇总清单都用相对路径；`AutoOut()` 改用 `TexTool.DefaultOut` |

### 验证（`_work\kkcard\tree\` 嵌套夹具）

```
tree\顶层卡.png
tree\A\a1\卡A-a1.png      tree\A\a2\卡A-a2.png
tree\B\卡B.png            tree\C\c1\卡C-c1.png
tree\A\A[zip]\已压过[zip].png     ← 陷阱①：上次的 [zip] 输出，必须跳过
tree\B\B[zip]\垃圾.png            ← 陷阱②：非卡片，也必须跳过
```

| 检查 | 结果 |
|---|---|
| CLI `batch tree`（省略 out_dir） | `找到 5 个 .png`（两个陷阱都没被算进去）；输出 `tree\tree[zip]\`，结构镜像：`顶层卡[zip].png` / `A\a1\卡A-a1[zip].png` / `A\a2\…` / `B\卡B[zip].png` / `C\c1\…` ✓ |
| 再跑一次（幂等） | 仍是 `找到 5 个`、`65179926 -> 16242938（24.9%）`，没有把自己的产出当输入 ✓ |
| `recurse=0` | `找到 1 个 .png`（只顶层）✓ |
| 显式 out_dir + `suffix=` | 输出保持原名、结构镜像 ✓；跑完后该目录带 `.koicardtex-outdir` ✓ |
| 有自定义输出目录后再递归 | 仍是 5 张（标记生效）✓ |
| `scan tree` | `共 5 个 .png（含子文件夹）`、`合计 70 张贴图` ✓ |
| GUI `uitest tree run=1` | `扫描完成：5 张卡，70 张贴图`、`OUTDIR: …\tree\tree[zip]`、`完成：成功 5 张…`，产出结构镜像、文件名 `[zip]`、无 `_2` 重名；`RECT : drop={X=0,Y=0,Width=1000,Height=30} content={X=0,Y=30,Width=1000,Height=760}` 与加勾选框前一致 ✓ |
| **回归**（9 张样本卡，`suffix=` 保持原名，新旧 EXE 对比） | 两次都是 `277896015 -> 41373883（14.9%）`，**10/10 SHA256 逐字节相同** ✓ |

### 坑

- 本轮又踩了两次**方括号通配符**：`Remove-Item -Recurse -Force "$T\tree[zip]"` 不报错也不删
  （路径被当通配符），结果 GUI 那次跑出 `顶层卡[zip]_2.png` 等一堆 `_2` 文件——不是工具的重名逻辑有问题，
  是我没删干净、勾选框「覆盖同名输出文件」又没勾。涉及 `[zip]` 的路径一律 `-LiteralPath`。
  `Set-Content -LiteralPath` 同理（不带 `-LiteralPath` 时连 `-Encoding` 都报 "parameter cannot be found"，
  因为 provider 动态参数没绑上）。
- 输出目录默认值必须**在枚举输入之前**算出来，否则排除不掉自己（第一版写成先枚举后取默认，会自我套娃）。
- 判断"这是我上次的输出"不能只看目录名：用户自定义 out_dir 名字任意，必须留标记文件。

## 18. 按部位压缩（第 2 个标签页）——只压某一件衣服的贴图

用户原话：「保留目前的UI布局和压缩方法，单独开个分页然后专门对单个服装进行调整。在新页面展示这个服装卡里面每个部位有什么贴图以及贴图信息。并且可以单独选择怎么压缩。」

### 为什么可行：贴图归属是数据自带的，不用猜材质名

`MaterialTexturePropertyList` 每条绑定都带槽位：

```
{'ObjectType': 1, 'CoordinateIndex': 0, 'Slot': 8, 'MaterialName': 'Metal',
 'Property': 'BumpMap', 'TexID': 5, 'Scale': [30.0, 30.0], ...}
```

`ObjectType 1` = 服装槽位、`2` = 饰品槽位（各自独立编号）。槽位名不是猜的，是从游戏
`E:\Koikatu-Game\Koikatu_Data\Managed\Assembly-CSharp.dll` 的嵌套枚举 `ChaFileDefine.ClothesKind`
读出来的（`[Reflection.Assembly]::ReflectionOnlyLoadFrom` + 枚举字段名）：

| Slot | 名字 | 中文 | Slot | 名字 | 中文 |
|---|---|---|---|---|---|
| 0 | top | 上衣 | 5 | panst | 连裤袜 |
| 1 | bot | 下衣 | 6 | **socks** | 袜子 |
| 2 | bra | 胸罩 | 7 | shoes_inner | 鞋（内层） |
| 3 | shorts | 内裤 | 8 | shoes_outer | 鞋（外层） |
| 4 | gloves | 手套 | 1000+n | （OT2） | 饰品槽 n |

映射自证：卡 A 里 `Skirt` 落在槽 1、`Bikini` 落在槽 3、`袖子/袖带` 落在槽 4、材质名 `Shoes` 落在槽 8；
卡 B 里 `袜子` 落在槽 5、`高跟鞋` 落在槽 7/8 ✓。（我一开始把槽 6 当"鞋"，被数据打脸：**6 是 socks**。）

### 实测：能切多细

卡 A（White&Deep_Blue 2，34 张 / 169.16 MB）：上衣 9 张/62.35 MB（独享 8 张）、下衣 6/25.10（3）、
胸罩 4/14.56（3）、内裤 5/8.05（4）、手套 5/20.86（**独享 0**）、袜子 5/20.86（**0**）、
鞋外层 7/19.63（4）、饰品槽 3/4/5 各 5~6 张（**全 0**）。

卡 B（KKCoordeF，82 张 / 62.18 MB）：12 个部位有贴图，鞋内层/鞋外层独享 0 —— 因为
`parts[7] == parts[8] == 100071017`（**同一件衣服穿在两个槽位**，贴图当然同一批）。
跨部位共享：卡 A 12/34 张（52.46 MB）、卡 B 22/82 张（12.23 MB）；最极端的是卡 A 的 `TexID 10`
被 **9 个部位**引用（只有 37.9 KB 的共用小图）。

### 语义（重点，逐条实测过）

1. **只压被勾选部位独享的贴图**；没勾的部位涉及的贴图逐字节不动。
2. **一张贴图被多个部位共用 → 必须那些部位全部勾选才会动它**；否则原样保留，
   并写明"被 X(未启用)、Y(未启用) 共用，那些部位选择不动它"。
3. 共用合并规则：**格式取最保守**（PNG < 自动 < JPEG，`Rank()`）、**最大边取最大**（更保画质）。
4. 独享 0 张的部位单独勾选 = 什么都不动（实测只勾手套 → 34 张全部未动）。
5. 全部勾选 ≈ 第 1 页整卡模式：`slots=all` 与不加 `slots` 的产物 **SHA256 逐字节相同**。

### 代码

| 位置 | 内容 |
|---|---|
| `src/Card.cs` | `ObjClothes/ObjAccessory`、`ClothesSlotNames[]`、`SlotKey/SlotObjType/SlotIndex/SlotName`、`sealed class Bind`、`List<Bind> SlotBindings(byte[])`；顺带修掉 `FindKey` 的 str8/16/32 bug（第 19 节） |
| `src/TexTool.cs` | `PartTex`/`PartInfo`/`PartsScan`、`ScanParts(string)`（贴图↔槽位双向索引；卡片没用到的 9 个服装槽也出行）、`MergeSlotRules(owners, slotRules, out merged, out why)`、`OwnerNames()`、`Rank()`；`Repack` **新增重载**带 `Dictionary<int,Rule> slotRules`，老签名保留并转发 `null` → 第 1 页零变化 |
| `src/Program.cs` | 新子命令 `parts <card>`；`compress … slots=部位:格式:最大边,…` + `ParseSlotRules`/`SlotKeyFromToken`/`SlotRulesWantsAll`；`uitest … tab2=1 [p2only=] [p2fmt=] [p2max=] [partsrun=1]` 无头自检 |
| `src/MainForm.cs` | `TabControl`：第 1 页 = 原 `root` 整块搬进 TabPage（**布局与逻辑一字未改**）；第 2 页 `BuildPartsPage()`：选卡 + `DataGridView` 部位表 + `ListView` 贴图明细（带「最终处理」实时预览）+ 独立输出/后缀/质量/保护 alpha + 日志；`LoadParts/FillPartsGrid/BuildSlotRules/FillTexList/DoPartsRun`；Drain 新增 `log2/status2/progress2/done2`；`Ui*` 无头自检接口 |

### 验证

| 场景 | 结果 |
|---|---|
| C# `parts` vs Python 探针逐项 | 部位数/贴图数/独享字节/材质名**完全一致**（两套独立实现互证） |
| 只勾「上衣 top」 | 169.16 → 110.35 MB；**只改 TexID 18~25**（上衣独享 8 张），其余 26 张含共享的 TexID 10 逐字节不变 ✓ |
| 只勾「手套 gloves」（独享 0 张） | **改动 0 张**，日志逐条说明原因 ✓ |
| 勾「手套+袜子」 | 只动 TexID 7/8/9/11（正好这两个共用）✓ |
| 手套 jpeg/512 + 袜子 png/2048 | 共用贴图按 **png/2048** 处理（产物确认 2048px PNG）✓ |
| `slots=all` vs 无 slots | 14,951,425 B，**SHA256 相同** ✓ |
| GUI 无头 `uitest … tab2=1` | 2 个标签页、10 行部位、明细含最终处理预览、`partsrun=1` 走完 DoPartsRun ✓ |
| **第 1 页回归** | CLI batch 10/10 SHA256 逐字节相同；GUI 拖目录同结果（62.16→37.63 MB）；`RECT : drop={0,0,1000,30} content={0,30,1000,760}` 不变 ✓ |
| 隔离环境（`DOTNET_ROOT=C:\no-such-dotnet`）`parts` | 682 ms ✓ |

### 坑

- 槽位号别按材质名猜：**6 = socks（袜子）、7/8 = 鞋内层/外层**。我第一次测试写 `slots=4:…,5:…`
  想指"手套+袜子"，5 其实是连裤袜（这张卡没穿）→ 一个都没压，差点误判成合并逻辑坏了。
- `slots=all` 若只覆盖 9 个服装槽会**漏掉整块饰品**（OT2 另一套编号）→ 改成按卡片实际部位展开
  （`SlotRulesWantsAll` + `ScanParts`）。
- 第 1 页的 `Repack` 老签名必须保留并转发 `null`，否则"保留目前的压缩方法"做不到零变化。

## 19. 顺手修的两个 msgpack 长键 bug（Python `find_key` / C# `Card.FindKey`）

`str8/16/32` 的编码是 `[marker][n 个长度字节][名字]`：**长度字节紧贴名字前面，类型标记在它前面**
（marker 在 `pos-n-1`，长度在 `pos-n..pos-1`）。原实现拿 `pos-1` 当类型标记判断，
于是**所有长度 >31 的键永远匹配不上**——包括 38 字符的
`com.deathweasel.bepinex.materialeditor`。今天只搜 17 字符的 `TextureDictionary`（fixstr）所以没暴露，
但按部位功能读更长的键必踩。修法：固定候选 n ∈ {1,2,4}，逐个检查 `mm[pos-n-1] == marker`
再读 `mm[pos-n:pos]` 的长度；修完 `find_key(GUID) = 156954 = 156916+38` ✓。

另外：GUID 那串字节可能以**值**的形式在别处也出现（值也是 str，长度前缀一样），
所以定位插件数据必须**遍历所有候选**、取能解析成 `[0, {map}]` 的那个（`kkcard_slot_stats.load_plugin`）。

## 20. 本沙箱里 `dotnet restore` 必挂 → 手写 assets + `--no-restore`

删掉 `obj/` 后 `dotnet build` 直接挂：

```
NuGet.targets(745,5): error : Value cannot be null. (Parameter 'path1')
   at NuGet.Common.NuGetEnvironment.GetFolderPath(NuGetFolderPath folder)
   at NuGet.Configuration.XPlatMachineWideSetting..ctor()
```

根因：这个 shell 里 **`PROGRAMDATA`/`ALLUSERSPROFILE`/`COMMONPROGRAMFILES`/`USERNAME` 等一批标准
Windows 环境变量是空的**，NuGet 解析机器级设置目录时拿到 null。显式补这些变量**无效**（试过），
别在环境上耗时间。

解法：本项目**没有任何 NuGet 包依赖**，assets 文件只是"框架引用 + 运行时包"的清单 →
用 `_work\kkcard_exe\make_assets.py` 生成，然后 `--no-restore`：

```
python make_assets.py full   # 带运行时包 → --self-contained true 单文件
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
  -p:PublishTrimmed=false -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:_RequiresILLinkPack=false --no-restore -o dist_sc
python make_assets.py lite   # 只带 apphost 包 → --self-contained false 单文件
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true \
  -p:PublishTrimmed=false -p:_RequiresILLinkPack=false --no-restore -o dist_lite
python make_assets.py full   # 收尾恢复（普通 build 也要用）
```

生成器按顺序踩的 4 个坑（每个对应一个报错）：
1. `NETSDK1047 资产文件没有 net8.0-windows/win-x64 的目标` → 必须有 RID 目标段。
2. `GenerateDepsFile … LibraryType.Parse(null)` → **每个 target 条目显式 `"type": "package"`**。
3. `MSB4006 循环依赖 ResolveProjectReferences` → 别把主项目写进 `libraries`（`msbuildProject` 等于自引用）；
   不写又 `ResolvePackageAssets.GetProjectReferencePaths` NRE。结论：**主项目不进 assets**。
4. `NETSDK1064 未找到包 Microsoft.NETCore.App.Host.win-x64` → apphost 包只在 SDK `packs/` 里有，
   要复制进 `.nuget` 并补齐 `.nupkg`/`.nupkg.sha512`/`.nupkg.metadata`/`<id>.nuspec`（`fake_package()`），
   且 native 资产**只列 apphost.exe**（整目录会多打 5 MB 的 nlib/h/pdb）。

`--no-restore` 下 `build` 与两种 `publish` 全部成功：SC 65,993,146 B、lite 253,528 B。

## 21. 单张贴图规则 + 预设导出 + 套用到同款服装（只有贴图不同）

用户原话：「在选中贴图明细之中加入可以更改单张贴图缩放规则的方法。并且加入在编辑完单张服装卡后保存目前对不同贴图的压缩方法为预设并导出，以及应用到相同服装（只有贴图不同）的批量处理的方式。以工作文件夹中的 CardA.png，CardA.png，CardA.png 为参考进行制作。」

### 先测数据：跨卡用什么键匹配

用 `_work\tex\kkcard_compare_cards.py` 比较三张参考卡（同款服装、不同贴图）：

| 比较 | 结果 |
|---|---|
| 卡1 vs 卡3 | 82/82 张、**78 个共有 TexID 的绑定完全一致**（另有 4 个 TexID 互不相同） |
| 卡1 vs 卡2 | TexID 集合差得多（`51~58` vs `100~107`）；53 个共有里 **14 个绑定不同**（卡1 多了 `.MECopy1` 材质副本）→ 卡2 是这套衣服的早期版本 |
| 按「部位\|材质\|属性」签名匹配 | 卡1↔卡3 83 个签名全中；卡1↔卡2 **59 个能对上**，且**签名无重复（不一对多）** |

结论：**用绑定签名当跨卡匹配键**，而不是 TexID 数字；并且把材质名的 `.MECopyN` 副本后缀归一化掉
（`NormMaterial()`），否则卡1 的 `袖子.MECopy1` 和卡2 的 `袖子` 会被当成两个材质。

### 语义（优先级从上到下）

1. **单张贴图规则**（明细表里一张一张指定）——最高；用户明确点过这一张，就压过部位规则与共用规则。
2. **部位规则**（部位表勾选 + 格式/边长）——共用贴图要求拥有者全勾，格式取最保守、边长取最大。
3. 都没有 → 原样不动。

### 代码

| 位置 | 内容 |
|---|---|
| `src/TexTool.cs` | `NormMaterial(s)`（去 `.MECopyN`）、`BindingSig(slotKey, material, prop)`、`Dictionary<object,List<string>> TextureSigs(byte[])`；`sealed class Preset { Source, Parts, Textures, List<Rule> Match(id, sigs) }` + `PresetTex { Id, Sigs, Rule }`；`PresetLoad/PresetSave`（System.Text.Json + 手写 JSON 输出，人可读）；`PartTex.Sigs`；`Repack` 再加重载带 `Preset preset`（`slotRules == null` 时用 `preset.Parts` 顶上；每张贴图先算部位合并规则，再用 `preset.Match()` 覆盖）；`RepackResult.PresetMatched/PresetMissed` + 收尾打印「预设：命中 N 张；未匹配 M 张（附清单）」 |
| `src/Program.cs` | 新子命令 `preset <card> <out.json> [parts=…] [tex=…] [source=…]`；`ParsePresetArg`；`compress`/`batch` 支持 `preset=file.json`（`batch` 里要把 `preset=` 从位置参数里滤掉，否则会被 `int.Parse` 当 max_size）；`uitest` 新增 `texset=行:格式:边长`、`savepf=`、`loadpf=`、`batchdir=` 四个无头入口 |
| `src/MainForm.cs` | 明细表从 ListView 换成 `DataGridView gridTex`：列 `TexID/类型/尺寸/字节/原格式/共用情况/处理方式/最大边/最终处理`，后两者是可编辑下拉框（首项「跟随部位」= 不覆盖），改过的行标黄；`texOv`（键=TexID 文本）存单张覆盖；`BuildPresetUi()` / `ApplyPresetToUi(pf, log)`（部位表 + 单张覆盖一起落地并报命中数）；按钮「保存预设…」「读取预设…」「用当前设置批量处理文件夹…」；`DoBatchPresetTo(dir)` 供无头调用 |

### 验证（三张参考卡）

| 场景 | 结果 |
|---|---|
| CLI `preset` 从卡1 导出（上衣 auto/512 + 鞋外层 png/1024 + 单张 TexID 26 png/256） | JSON 正确写出，含每条规则的绑定签名 ✓ |
| `batch` 三张卡套用该预设 | 3/3 PASS；**预设命中 3 / 未匹配 0**（卡2 的 TexID 平移也能对上）✓ |
| 逐 TexID 比对改动 | 每张卡**都只改 5 张**：`16,21` + 上衣独享的 2 张（卡1=`91,94`，卡2/卡3=`55,56`）+ 单张指定的 `26` ✓ 与"上衣独享 ∪ 预设单张规则"逐项吻合 |
| GUI 明细里把上衣那行 6 张贴图设成 PNG/256 | 每行显示「单张指定 png/256」、Y 标黄、`TEXOV` 记录 6 条 ✓（其中 TexID 22/36 是跨 3~10 个部位共用的，单张规则照样生效） |
| GUI 保存预设 → 载入卡3 | **单贴图命中 7 张、未匹配 0**：卡1 的 `91/94` 正确落到卡3 的 `55/56` ✓ |
| GUI「用当前设置批量处理文件夹」vs CLI `batch preset=` | 产出**字节数完全一致**（61401229 / 61172060 / 60856804）✓ |
| 隔离环境（无 .NET）部署版跑界面批量 | `批量完成：成功 3 张，失败 0 张；预设命中 3 / 未匹配 0` ✓ |
| 回归 | 第 1 页 CLI batch 10/10 SHA256 逐字节相同；`RECT` 不变；只勾上衣仍是 110.35 MB、只改 8 张 ✓ |

### 坑

- `batch` 的位置参数解析要把 `preset=` 滤掉，否则 `int.Parse("preset=…")` 直接
  `FormatException: The input string was not in a correct format`（第一次就这么挂的）。
- C# 的 `Usage()` 是**逐字字符串**（`@"..."`），里面写英文双引号会把字符串截断
  （`CS1003/CS1010/CS1056` 一串语法错误）——要么不写引号，要么写成 `""`。
- 单张贴图规则会**跨共用关系生效**（这正是用户要的"这一张我说了算"），所以日志必须写清
  「[预设] TexID n: 单张指定 …」，否则用户会以为共用保护失效了。
- 预设匹配是"绑定级"的：一条预设规则可能命中目标卡上的**多张**贴图（它们共用同一个绑定签名），
  这是有意为之（同一材质同一属性用同一处理），命中数会 > 预设条目数，不是 bug。

## 22. 通用设置 / 一键统一 / 同款检测 / 子文件夹开关（1.6）

用户原话：「在套用当前预设设置批量处理文件夹的时候也需要加入一个是否应用到子文件夹。在批量压缩和按部位压缩中都添加一键设置处理方式的选项，如统一设置尺寸或格式。在按部位压缩的选项加入一个通用设置。在进行压缩之前先检测是否和当前预设中的服装卡属于同款服装，如果不是的话跳过这个服装，同时加入选项来强制将目前通用设置应用到不匹配的服装中。」

### 同款检测：阈值是量出来的，不是拍的

用 `kkcard_compare_cards.py` 的口径算「预设签名 ∩ 卡片签名 ÷ 预设签名」：

| 卡 | 与基准（卡1）的匹配度 |
|---|---|
| 卡3（同款换贴图） | **100.0%** |
| 卡2（同款早期版本） | **94.1%** |
| White&Deep_Blue 2（无关服装） | **0.0%** |
| CardA.png（另一套） | **0.0%** |

分离度极大 → 默认阈值 `minMatch = 0.5`，写进预设 JSON 可手改（也支持 `minmatch=50` 写成百分数）。

**踩到的坑（重要）**：预设最初只存"有单张贴图规则的那几条签名"，于是一个**只写了部位规则的预设**
（比如 `parts=top:auto:512`）根本没有可比对的签名集合 → `Score()` 走"没记签名就不拦"的兜底 →
无关卡被**照压不误**（实测 `White&Deep_Blue 2` 被压成 63.6% 而不是跳过）。
修法：预设里单独存一份**整卡签名指纹** `"sigs": [...]`（`Preset.SigSet`，导出时取该卡全部贴图的签名并集），
`AllSigs()` 优先用它、没有再退回单张贴图规则里的签名。

### 语义

- **通用设置**（`Uniform`）：**只给"不匹配的服装"用**（勾了「不匹配服装使用通用设置压缩」时整卡按它压）。
  它**不参与**同款卡的部位/单张贴图规则，也不会出现在明细的「最终处理」列里。
  （1.6 第一版做成"勾上＝整卡统一、部位表停用"，用户否掉了：通用设置是给不匹配的卡用的，
  不该影响主要部位设置。1.7 已改成上面这条语义。）
- **一键统一**：第 1 页「应用到所有类型」；第 2 页「应用到所有部位」「应用到本部位全部贴图」。
- **子文件夹开关**：只影响"用当前设置批量处理文件夹…"的枚举范围（`chkSub` → `TexTool.Enumerate(inDir, null, chkSub.Checked)`）。
- 新返回码 **Rc=6 = 与预设不是同款服装，已跳过**：进「跳过清单」（不是失败），
  `RcText(6)` 有对应文案；CLI batch 里 `Rc==3 || Rc==6` 都算跳过。
  勾了"用通用设置压"但通用设置是「原样不动」时也走 Rc=6，理由写成
  「与预设不是同款服装（匹配度 x%），且没有可用的通用设置」。

### 验证

| 场景 | 结果 |
|---|---|
| 三张同款卡 + 1 张无关卡，`batch preset=`（不勾通用设置） | 2 张同款 94.7% PASS；`CardA.png 跳过（不是同款服装）`，理由写「匹配度 0.0%，要求 ≥50.0%」；产出目录里没有它 ✓ |
| 同上勾「不匹配服装使用通用设置压缩」（`force=1`） | 3/3 PASS；无关卡 177,381,281 → **8,725,468（4.9%）**、34/34 张贴图全按通用 `auto/768` 压（上衣 62.35 MB → 1.91 MB）✓ |
| **同款卡不受通用设置影响的 A/B**（同一套界面规则跑两次，只切换"勾/不勾通用设置"） | 同款卡产出**逐字节相同**（20,556,013 / 17,658,537 两轮一致）✓；不匹配卡第一轮跳过、第二轮 8,725,468 ✓ |
| 勾了"用通用设置压"但预设里没有通用设置 | 仍跳过，理由「…，且没有可用的通用设置」✓ |
| GUI 一键 `allparts=PNG:512` | 部位表全部行变成 启用=True / PNG / 512 ✓ |
| GUI 批量 + `sub=1`（2 张同款 + sub\ 里 1 张 + 1 张无关） | 产出里有 `sub\` 镜像 ✓；`sub=0` 时子目录根本不读 ✓ |
| 部署版 1.7 隔离环境复跑 | `批量完成：成功 3 张，跳过 0 张，失败 0 张`，`[不匹配服装] …→ 按通用设置 auto/768 整卡压` ✓ |
| 回归 | 第 1 页 batch **10/10 SHA256 逐字节相同**；`RECT` 不变；只勾上衣仍 110.35 MB ✓ |

### 坑

- 预设只记"有规则的签名"→ 同款检测失效（见上）。凡是要靠指纹判断的东西，必须在导出时
  **无条件**把整卡指纹写进去，不能只写"被改过的那些"。
- `batch` 的位置参数解析除了 `preset=`/`force=`/`minmatch=` 也要滤掉，否则 `int.Parse` 抛
  `FormatException`（这轮又差点踩一次）。
- 强制模式要**忽略**预设的单张贴图规则（`&& !forced`），否则会把同款的单张规则套到别的衣服上。
- 验证"X 不影响 Y"这类命题时，**A/B 两轮必须用同一套规则**再比字节；我第一轮拿"界面默认
  auto/1024"去比"CLI 预设 512 的产物"，差异是规则不同造成的，白忙一场。
- 又一次 **方括号通配符**：`Copy-Item -Recurse "$M\mixed4[zip]\*" $H` 的**源路径**带 `[zip]`
  被当通配符 → 什么都没复制（`Get-Item` 又没加 `-LiteralPath` → 长度 0、哈希 null →
  一度看着像"逐字节不同"）。处理带 `[zip]` 的路径：`-LiteralPath`，或者干脆
  `Rename-Item -LiteralPath … -NewName holdX` 换个不带方括号的名字再操作。

## 23. 界面重排：多列并排 + 日志整块可折叠（1.8）

用户原话：「重新设计UI，首先是将批量压缩中的日志显示移动到右边单独整块区域，并且可以使用按钮隐藏。按部位压缩中的选择服装卡和输出/选项等没有显示框的内容放在左边。部位一览放在中间，选中部位的贴图明细放在右边，日志放在最右边并且一样做成可以通过按钮隐藏的样式。」

### 布局

两页都用 `TableLayoutPanel`，**日志永远是最后一列**（`Absolute, LogWidth=340`），
日志面板 `Dock=Fill` + `SetRowSpan(..., 行数)` 占满整高。

| 页 | 列 |
|---|---|
| ① 批量压缩 | 左=输入/类型表/选项/按钮（Percent 100）＋ 底部进度状态；右=日志（340，可折叠） |
| ② 按部位压缩 | 左=选卡 + 输出/选项（Absolute 360）；中=部位一览（50%）；右=贴图明细（50%）；最右=日志（340，可折叠）；底部=跨 4 列的动作条 |

折叠（`ToggleLog(int which)`）：`w.Visible = show` **且** `ColumnStyles[last].SizeType=Absolute; Width = show ? 340 : 0;`
——只改 `Visible` 会留一条空白列。释放出来的宽度由 Percent 列自动瓜分。
折叠按钮放在**该页底部动作条里**（收起后仍可见），日志块头部再放一个「收起 ▶」。
默认窗口从 1000×790 放大到 1360×840（4 列在 1000 宽下每列只剩 ~150px）。

### 两个必踩的坑

1. **`Control.Visible` getter 是"有效可见性"**：控件只要有祖先不可见（例如它在没被选中的 TabPage 里）
   就返回 false。原来写成 `bool show = !w.Visible;` —— 在第二个标签页上永远判成"要展开"，
   于是日志**关不掉**。改成自己维护 `bool log1Shown/log2Shown` 状态字段，不读 `Visible`。
2. **`TableLayoutPanel.Controls.Add(ctl, col, row)` 越界会被悄悄夹格**：`Add(gbTex, 0, 2)`
   加到只有 2 行的表里 → 落到 (0,1)，贴图明细跑到左列底部而不是右列。
   编译不报错，只能靠坐标自检发现：`UiLayoutDump` 打印各块 `Bounds` + 末列宽。

### 验证（无头）

```
LAYOUT : tab1[1344x772] log1={X=999,Y=11,W=334,H=750} logColW=340 | tab2[1340x768]
         左列={X=7,Y=7,W=354,H=715} 部位表={X=367,W=310} 明细={X=683,W=310} 日志={X=999,W=334}
LOGTGL : 收起后 log1=False log2=False  … 部位表 W=480 明细 W=480 日志列宽=0 … 再展开 log1=True log2=True
```

功能回归：第 1 页 CLI batch **10/10 SHA256 逐字节相同**；第 2 页部位读取/单张覆盖/按部位压缩照常
（`82 张贴图 17 个部位`、`[OK] 校验通过`）；部署版 1.8 在隔离环境（`DOTNET_ROOT` 指向不存在目录）
下布局与折叠均正常。

## 24. 界面 1.9：真·开关式日志按钮 + 可拖动分隔条

用户原话：「按部位压缩的显示日志按钮无法正常工作。将按钮改为按下显示日志后再按下就能隐藏日志。将批量压缩中的显示日志单独挪到最右边。在按部位压缩中的 部位一览 和 选中部位的贴图明细 上加入可以拖动边缘放大或缩小相应显示框的功能。」

### 1.8 的日志按钮为什么"不工作"

两个原因叠在一起，都是布局层的：

1. **第 1 页的 `root` 还是 `ColumnCount = 1`**（重排时那处替换没生效）→ `MakeSplitter(root, 1, 2, …)` 的
   `Controls.Add(sp, 1, 0)` 和 `Controls.Add(logWrap1, 2, 0)` 都被 **静默夹到第 0 列**，
   `SetLogVisible` 又去改 `ColumnStyles[Count-1]`（＝左内容列）的宽度 → 按了按钮等于改内容区宽度。
2. 按钮是普通 `Button` + 文案切换，没有"按下=显示中"的状态反馈，用户不容易判断当前状态。

修法：切换按钮改成 `CheckBox { Appearance = Appearance.Button }`（外观是按钮、勾上就是按下状态），
文案恒定为「隐藏日志 / 显示日志」二态，放在**该页底部动作条的最右边**（收起后仍可见）。
第 1 页的日志列改回 3 列结构：`[操作区 Percent][6px 分隔条][日志 Absolute 340]`。

### 2. 可拖动分隔条：不用 SplitContainer

第一版用嵌套 `SplitContainer`（3 条分隔条），编译能过、`dotnet build` 也没问题，
但一跑就抛：

```
System.InvalidOperationException: SplitterDistance 必须在 Panel1MinSize 和 Width - Panel2MinSize 之间。
   at System.Windows.Forms.SplitContainer.ApplyPanel2MinSize(Int32 value)
```

原因是未选中的 TabPage/构造期布局时容器宽度可能是 0，而 WinForms 会在**内部布局路径**里应用 MinSize
—— 把赋值全包进 try/catch、把 MinSize 设成 0 或 110 都没用（内部照样抛）。
最终改成 **TableLayoutPanel + 自绘 6px 分隔条**（`Panel{Cursor=VSplit}` + MouseDown/Move/Up），
拖动应用逻辑抽成 `Action<int>` 存在 `Dictionary<Panel, Action<int>>` 里，鼠标事件与自检**共用同一份逻辑**。

第 2 页列结构（7 列）：

```
[0]左列 Absolute 360  [1]条6  [2]部位一览 Percent 50  [3]条6  [4]贴图明细 Percent 50  [5]条6  [6]日志 Absolute 340
```

- 左列 | 部位一览：左列是 Absolute → 拖它改左列宽度（`Max(160, startL+dx)`）
- 部位一览 | 贴图明细：两边都是 Percent → 按像素比例换算成百分比（互相吃）
- 贴图明细 | 日志：日志是 Absolute → `Max(140, startR - dx)`（往左拖变宽）
- 折叠日志：日志列宽 0 + 面板隐藏 + **连它的分隔条一起隐藏**，Percent 列自动铺满。

### 3. 两个"测试绕过真实路径"的假通过（这轮最值钱的教训）

| 假通过 | 真相 | 修法 |
|---|---|---|
| `UiDragSplitter` 直接设列宽，"拖动正常" | 真正的鼠标回调改错了列（改的是 6px 分隔条自己那列）→ **真实拖动完全无效** | 把应用逻辑抽成 `Action<int>`，自检传 dx 调同一份逻辑 |
| 布局 dump 打印各块坐标，"坐标对" | 第 1 页 root 只有 1 列，控件被静默夹格，日志其实叠在内容上 | dump 必须同时打印 `ColumnStyles` 数量/宽度，别只看控件 Bounds |

另外两个小坑：`CheckBox` **没有** `PerformClick()`（编译报错，自检里模拟点击要"先翻 `Checked` 再调 Click 处理"）；
`Get-FileHash` / `Copy-Item` / `Remove-Item` 碰到含 `[zip]` 的路径一律要 `-LiteralPath`（本会话第 N 次踩）。

### 验证（部署版 1.9、隔离环境）

```
LOGTGL : 初始 log1=True log2=True w1=340 w2=340
LOGTGL : 点一次后 log1=False log2=False w1=0 w2=0   （中间两块 298→468 自动铺满）
LOGTGL : 再点一次 log1=True log2=True w1=340 w2=340
SPLIT  : 各条 +80/+120/-140/-60 后  左列 354→428、列宽[0] 360→434、日志 334→470、列宽[6] 340→475、tab1 日志 340→394
SPLIT  : 反向拖回后                  全部回到原值附近
```

功能回归：第 1 页 CLI batch 10/10 SHA256 相同；第 1 页 GUI 拖目录 5 张 62.16→37.63 MB；
第 2 页部位读取/单张覆盖（只改 9 张：10,18..25）/按部位压缩 `[OK] 校验通过`；预设读取命中 1 张未匹配 0。

## 25. UI 2.0：统一设置单独一行 + 括号截断真因 + 拖动"越拖越飞"修复

用户原话：「批量压缩中的统一设置单独放一行。UI文字中有多余的括号出现，如整个文件夹（，保护 alpha(。在按部位压缩中拖动显示框边缘工作的情况异常，在我拖动的时候只要按住鼠标稍微移动一下，整列就会很快的移向一边，我需要的是跟着我鼠标的移动来拖动显示框。」

### 1) 拖动"越拖越飞"：起点宽度被反复重测

上一轮我把拖动逻辑抽成 `Action<int>` 时，把**起点宽度也移到了委托内部**：

```csharp
// ✗ 每次 MouseMove 都重新量当前宽度当起点，再把同一个 dx 加上去 → 位移被反复累加
splitDrag[sp] = delegate (int dx) {
    int startL = ColPixels(t, leftCol);          // ← 错
    t.ColumnStyles[leftCol].Width = startL + dx;
};
```

修法：`splitBegin[sp]` 在 MouseDown 时量一次起点（`startX/startL/startR`），
`splitDrag[sp]` 只用它们 + 总位移；并用 `sp.Capture = true` 代替自建 dragging 标志
（鼠标移出控件也不丢事件、松开自动结束）。

**自检必须模拟"一次按住 + 多次 MouseMove"**，一次设个大位移是测不出来的：

```csharp
public void UiDragSteps(int which, params int[] steps) { beg(); foreach (var d in steps) drag(d); }
```

实测（部署版 2.0）：一次拖动 8 步共 -80 → 日志列宽 330 → **405**（理论 410，差 5px 是"面板宽 vs 列宽"口径差）✓
；另一条 +120 → 部位表 298→383、明细 298→159（按 70/30 分）✓。

### 2) "多余的括号"＝控件被截断，不是字符串写错

我先把所有 `Text = "…"` 的字符串扫了一遍括号是否配平 —— **全部配平**（我的正则还在 `1)`、`2)`
这种编号上误报了一轮）。真正原因是控件宽度不够：`RadioButton 整个文件夹（批量）` 需要 124px 实际只有 104px
→ 屏幕上就显示成 `整个文件夹（`；`保护 alpha（…）` 同理。

**判定用自检，不靠肉眼**：

```csharp
int need = c.PreferredSize.Width;                 // 用它，别用 TextRenderer.MeasureText + 8
if (need > c.Width + 2) → 记为截断
// 只查 Label/CheckBox/RadioButton/Button/GroupBox；RichTextBox/TextBox/DataGridView 本来就能滚动，排除
```

修法（两条都用了）：
- 缩短文案：`整个文件夹（批量）`→`整个文件夹`、`保护 alpha（含透明的走 PNG）`→`保护 alpha`、
  `包含子文件夹（递归，输出保持同样结构）`→`包含子文件夹`、`覆盖同名输出文件`→`覆盖同名`
- 把挤在一行的勾选框拆成各自一行（tab 2 左列新增 `rChk` / `rSub` 两行）

结果：两页都打印「（没有文字被截断）」。

### 3) 统一设置单独一行

第 1 页的 root 从 5 行改 6 行：`输入/输出 | 类型表 | 质量/预设 | 统一设置 | 按钮 | 进度状态`，
统一设置从 `pOpt` 里挪到自己的 `pUni1` 流式面板（`统一设置（一键改所有类型）：[格式][边长][应用到所有类型]`）。

## 26. 人物卡压缩（第 3 页）：角色本体 + 7 套换装

用户原话：「增加一页新功能，人物卡压缩。人物卡里面包含了角色材质的贴图和多套不同的衣服。要实现的功能有批量压缩选中人物卡贴图，以及按单个人物卡压缩。单人物卡压缩要可以选择压缩哪套的衣服的什么贴图。然后需要注意的是人物卡本身自带了人物的身体贴图材质。你需要以工作区的 CardA.png_DX4 FOR LORA_DX4.png 和 CardA.png_natsu.png 这两张角色卡为例子进行编写。」

### 人物卡的真实结构（两张参考卡实探）

`_work\tex\kkchara_deep.py` / `kkchara_groups.py` 探出来的：

- 附加数据标记是 `【KoiKatuChara】`（衣服卡是 `【KoiKatuClothes】`）；
  **人物卡在版本号后面没有"卡名"字符串**（衣服卡有）→ `Card.ReadInfo` 原来按衣服卡读，
  会把文件内容当长度读出一段乱码卡名（已修：`Info.IsChara`）。
- 贴图只有**一个** TextureDictionary：LORA 卡 **68 张 / 229.17 MB**，natsu 卡 **93 张 / 269.5 MB**。
- `MaterialTexturePropertyList` 里 299~515 条绑定，`ObjectType` 有三类：
  **1 = 服装槽、2 = 饰品、≥3（实测 4）= 角色本体**（`cf_m_body` 身体、`cf_m_eyeline_*` 眼线、
  `cf_m_hitomi_00` 瞳孔、`cf_m_sirome_00` 白目）；`CoordinateIndex` **0..6 = 第几套换装**
  （School01/School02/Gym/Swim/Club/Plain/Pajamas，与游戏 `ChaFileDefine.CoordinateType` 一致）。
- **同一张贴图会被多套换装共用**（natsu 卡 93 张里 157 个「组·部位」组合），
  所以"只压第 N 套"必须沿用「所有拥有者都启用才动」的既有规则。

### 键的编码（踩过坑）

规则键 = `Chara.PackKey(group, slotKey)` = **`(group+1) * 100000 + slotKey`**。
`+1` 不能省：组 0 是「角色本体」，若直接 `group*100000`，本体键就落在 `slotKey` 上，
与衣服卡的槽位键（<1000）撞车 → `Chara.IsCharaKey()` 判 false → 分组全乱
（表现：日志里出现 `【?】` 与"身体/脸/头发/眼睛(未启用)"这种驴唇不对马嘴的名字）。
另外 `Card.SlotKey` 也扩了一档：`ObjType ≥ 3 → 2000+slot`，这样本体的部位名不再是"上衣 top"。

### 实现（复用为主）

- `src/Chara.cs`（新）：`IsCharaCard/IsClothesCard`、`Bindings()`（**把所有 `MaterialTexturePropertyList`
  出现位置都扫一遍**，只取值是 bin 且能解析成数组的）、`GroupOf/GroupName/PackKey/KeyGroup/KeySlot/KeyName`、`BodySigs`。
- `TexTool`：`KName(key)`（人物卡键带组名）、`TexKeys(b, chara)` / `SigMap(b, chara)`（衣服卡用槽位键、
  人物卡用组·部位键）、`ScanParts` 按卡片种类分组、`Repack` **自动识别**卡片种类并自动用对应的键来源。
- `Card.FindKeyFrom(b, name, from)`：新增起点参数，供"同一键出现多次"的扫描。
- CLI：新命令 `chara <card>`（列出本体/换装统计）、`groups=all|body|0,5`（compress/batch 通用）。
- GUI：`src/MainFormChara.cs`（新，`partial class MainForm`）→ 第 3 页 = 左（选卡可多选 + 输出/选项）
  / 中（组·部位表）/ 右（贴图明细，可单张指定）/ 最右（日志可折叠），结构与第 2 页一致，
  自带 3 条可拖动分隔条。

### 这一轮修掉的 4 个 bug（用户报"人物卡压缩没法正常工作"）

| # | bug | 表现 | 修法 |
|---|---|---|---|
| 1 | **`Drain()` 缺第 3 页的消息分支**（log3/status3/progress3/done3）——我用 PowerShell `.Replace` 插入时锚点没匹配，`Replace` 不报错 | worker 的日志与 `done3` 全被静默丢掉 → 日志不刷新、**按钮一直灰着不可交互**、看着像卡死 | 补上 4 个 case。教训：批量文本替换后要 **grep 验证**，不能只看"脚本跑完了" |
| 2 | 同款检测用了错误签名口径：预设指纹是**人物卡**签名（`换装3 Gym · 上衣 top|材质|属性`），而 `Preset.CardSigs(b)` 生成的是**衣服卡**签名（`top|材质|属性`）→ 交集恒为 0 | 套预设压人物卡 → 匹配度 0% → **每次都"跳过"**（GUI 总会带预设，CLI 不带才没暴露） | 预设加 `IsChara`（JSON 里 `cardType`），`Repack` 先按**卡片种类**判断：种类不同才跳过；人物卡预设对任何人物卡直接放行 |
| 3 | 人物卡之间用"签名重叠度"判断同款 → 两张不同角色卡只有 **28.6%** | 跨卡批量会把真的该压的卡判成"不是同款"跳过 | 同上：人物卡的「组·部位」规则与穿什么衣服无关，不做同款检测 |
| 4 | 窄面板下 `AutoSizeColumnsMode = Fill` 把 9 列压成几个像素（截图里表头变成 `Te\|类\|尺\|字\|原\|…`） | 明细表读不出来 | 两页的表格都改成**显式列宽 + `ScrollBars.Both`**（横向滚动），不再压缩列 |

另外顺手：`Cli.W` 的日志文件加 `AutoFlush = true`（长任务中途也能看到进度，
不然缓冲区没刷，会误判成"卡住不动"）；第 2/3 页的 `DataGridView` 加**重入守卫**
（清行 → 触发 `CellValueChanged` → 又回去清行 = **StackOverflow**，实测踩到过）。

### 验证（两张参考卡）

| 场景 | 结果 |
|---|---|
| CLI `chara` 列组 | LORA：本体 4 张/2.68 MB + 7 套换装（Gym 35.84 MB、Swim 161.83 MB、Club 123.38 MB…）；natsu：93 张/157 个组·部位条目 |
| CLI `groups=body`（natsu） | 只改 TexID **19,2,3**（本体贴图），其余 90 张逐字节不变；284003723 → 283922364 |
| CLI `groups=3`（LORA 的 Gym） | 只改 TexID **41,51**；241206277 → **217420185**（-24 MB = 该套贴图的量） |
| 预设路径（= GUI 路径） | `[同款]/[人物卡] 按预设的「组·部位」规则处理` → 成功 1 张、只改 2 张 |
| GUI 第 3 页（`uitest tab3=1 g3only=0 g3run=1`，natsu） | `人物卡完成：成功 1 张，跳过 0 张，失败 0 张`，产物 283922364 字节，只改 3 张本体贴图 |
| GUI 批量（natsu + LORA 两张不同角色卡，只勾本体） | `成功 2 张，跳过 0 张`（不再误判"不是同款"） |
| 回归 | 衣服卡 batch **10/10 SHA256 逐字节相同**；第 2 页只勾上衣仍 110.35 MB `[OK]`；第 1 页 GUI 拖目录 5 张 60.5%；布局/折叠/截断自检全绿 |

---

## 27. 2.2：人物卡页收尾（改名 / 同目录输出 / 组过滤一键统一 / 缩略图预览）+ 批量页评估

用户原话：「人物卡压缩的名字改为单张人物卡压缩。然后去掉选择文件夹相关的按钮。把批量压缩人物卡的功能应该放在批量压缩中，评估现有的批量压缩是否可以满足压缩人物卡的功能。单张人物卡压缩的默认输出目录应该和卡片路径在同一个文件夹内。同时加入其他模块中一样的一键统一的功能统一设置压缩方式，但需要加入可以对不同组过滤使用的功能。然后再思考一下，可以做到在按部位压缩和单张人物卡压缩页面时点击选中部位贴图明细的时候显示这个贴图的缩略图功能吗？」

### 27.1 第 3 页改名 + 去掉选文件夹

- `TabPage` 标题 `③ 人物卡压缩（本体 + 换装）` → **`③ 单张人物卡压缩`**。
- 删掉 `选文件夹…` 按钮（连同它的 `FolderBrowserDialog` + `TexTool.Enumerate` 分支）；
  `浏览…` 也去掉 `Multiselect = true`（要一次多张就拖进来，拖入路径仍支持多选 + 目录递归，
  目录递归看 `chkG3Sub`，文案改成「拖目录时含子文件夹」）。补了个 `清空` 按钮。
- **输出目录默认 = 卡片自己所在的文件夹**：`Path.GetDirectoryName(card)`（原来是
  `TexTool.DefaultOut()` = 卡片旁边的「压缩输出」子目录）。实测 `G3OUT : _work\kkcard\eval_c`
  且产物直接落在那里。

### 27.2 一键统一 + 组过滤（只改指定的组）

中列顶部新增一行：`统一范围 [全部组▾] 处理方式 [自动▾] 最大边 [1024▾] [一键统一]`。

- 范围下拉 = **全部组 / 角色本体 / 换装1 School01 / … / 换装7 Pajamas / 仅当前选中组**，
  从当前卡的 `ps3.Parts` 动态生成（`G3FillUniScope()`，与「组过滤」下拉同一套组名）。
- `G3UniApply()`：遍历 `gridG`，按 `row.Tag`（= 组·部位键）取 `Chara.KeyGroup()` 判断是否命中范围，
  命中才写 `fmt`/`max` 两格；**范围外的组一个字节都不改**。同时清掉**作用范围内**贴图的
  「单张指定」（`texOv3` 的键是 TexID，所以是先由 `ps3.Parts` 反查该组有哪些 TexID 再删，
  不能拿 TexID 直接当组键用）。
- 组合框条目不能用 `TexFmtItems`/`TexSizeItems`（那两个含「跟随部位」，是明细表专用），
  新增 `G3FmtItems`/`G3SizeItems`（不带跟随部位），否则写进组·部位格会得到非法值。

实测（natsu，157 个「组·部位」）：

| 统一范围 | 结果 |
|---|---|
| 换装3 Gym / JPEG / 512 | `22 个条目` → 表里 换装3 全 JPEG、其余 7 组仍 `自动`（逐组统计确认） |
| 仅当前选中组（选中第一行=本体）/ PNG / 2048 | `1 个条目` → 只有「角色本体 · 身体/脸/头发/眼睛」变 PNG |
| 全部组 / JPEG / 1024 + 真跑压缩 | 157 个条目全改；284,003,723 → **22,214,929 B（7.8%）**，`人物卡完成：成功 1 张` |

### 27.3 缩略图预览（第 2、3 页都做了）

- 共用 `UpdatePreview(pic, info, grid, cardPath)`：取 `grid.CurrentRow.Tag`（TexID 字符串）
  → `TexTool.TextureBytes(cardPath, id)` → `LoadTextureImage`（HDR 走 `HdrToBitmap`）
  → 等比缩到预览框再 `DrawImage`，**并释放上一张 Bitmap**（不把 4096² 原图留在内存）。
- 布局：明细表所在的 `GroupBox` 里塞一个 `TableLayoutPanel`（表 Percent 100 + 6px + 预览 240px），
  预览面板内是 `PictureBox{Dock=Fill, Zoom}` + `Label{Dock=Bottom, Height=34}`。
- 事件：`gridTex.SelectionChanged`（第 2 页）/ `gridGT.SelectionChanged`（第 3 页）；
  重入由既有的 `previewBusy` 守卫挡住。
- 自检钩子：`UiP2PreviewAt(i)` / `UiG3PreviewAt(row)`，命令行 `uitest … p2prev=3 / g3prev=6`。
  实测每行都出图：`PREV3 : 行 0 → 角色本体 · 身体/脸/头发/眼睛 → 图=190×190 说明=TexID 2 190×190 193.27 KB PNG`。

### 27.4 批量页（第 1 页）能不能压人物卡？—— 能，已实测

结论：**不用为人物卡单独做批量页**。引擎按卡片头自动认卡种（`Chara.IsCharaCard`），
第 1 页本来就是通用批量页，人物卡直接整卡压；2.2 只补了一行**「人物卡组过滤」**
（`chkGrp[8]` + 全选/只压本体/全不选 → `CharaMask()`，作为 `Repack` 的第 12 个参数）。

| 验证 | 结果 |
|---|---|
| batch 整卡（natsu，284,003,723 B） | → **22,843,999 B（8.0%）**，与 2.1 单卡产物**字节数完全一致** |
| 同目录里的衣服卡 | 65,624,725 → 20,556,013；带 `groups=body` 与不带时产物 **SHA256 相同** |
| `groups=body` 的逐 TexID 比对 | **只改 TexID 19 / 2 / 3**（本体），其余 **90 张逐字节不变** |
| 第 1 页 GUI（`uitest … run=1 grp=body`） | `[组过滤] … 原样保留` 90 条；产物只改 1 张主贴图（类型表里其余类 = 原样不动），落在 `eval_c[zip]` |
| 人物卡预设套 batch | 命中并把衣服卡跳过：`[跳过] —— 预设是人物卡预设，本卡是衣服卡` |
| 老预设没有 `cardType` | `PresetLoad` 现在用「部位键是否 ≥100000（组·部位键）」**反推卡种**，否则人物卡预设会被误判成衣服卡预设而全部跳过 |

> 掩码语义：`charaGroupMask != 0 && charaCard` 才生效 → **衣服卡完全不受组过滤影响**，
> 所以「人物卡 + 衣服卡混在一个文件夹里批量」是安全的。

### 27.5 回归与发布

| 项 | 结果 |
|---|---|
| 衣服卡 batch 回归（samples 10 张，auto 1024 q90 1024 `suffix=`） | `277896015 -> 41373883（14.9%）`，与 2.1 完全一致，**10/10 SHA256 逐字节相同** |
| 布局/截断自检 | 第 1/2/3 页 `CLIP : （没有文字被截断）`（第 3 页含新加的组过滤行与统一行） |
| 日志开关 / 拖动分隔条 | `LOGTGL` 三次状态与列宽 340→0→340；`DRAG1` 一次拖 -80px 终点 = 起点+80 ✓ |
| 发布 | `KoiCardTexTool2.2\`（SC 66,022,246 + lite 332,745 + plan + 使用说明）+ `kk服装卡贴图压缩2.2.zip`（60,423,369，含 SC exe / plan / 使用说明） |

顺带踩到 / 记住的两条：
1. `PowerShell -RedirectStandardOutput` 起 WinExe 在本沙箱里报 `拒绝访问`（命名管道限制），
   改回 `& .\x.exe args 2>&1 | Out-String` 就能拿到输出（管道会让 PowerShell 等它跑完）。
2. `RichTextBox.Text` 的换行是 **`\n`**，按 `"\r\n"` 切行会得到一整块 → 自检里找日志行
   要 `Split(new[] { "\r\n", "\n" })`。

---

## 28. 「贴图比设定上限还小的时候，还会被压吗？」——语义 + 汇总口径修正

用户问：「如果选择压缩的贴图尺寸小于设定的尺寸，贴图还会被压缩吗」

### 语义（代码位置 `TexTool.Repack`）

- 缩放判据只有一句：`sc = (cap > 0 && Math.Max(w, h) > cap) ? cap / Math.Max(w, h) : 1.0`
  → **大边 ≤ 上限就完全不缩放**（也不会放大），`tw/th` 保持原尺寸。
- 但**重编码照做**：`pngNew = EncodePng(small)` 永远执行，`useJpg` 时再算 `jpgNew`；
  格式选择与尺寸是两件独立的事。
- 兜底：`if (use.Length >= raw.Length) { use = raw; fmtName = "orig"; }`
  → **重编码后不比原字节更小就原样透传**（`auto` 模式下 JPEG 比 PNG 大也会退回 PNG）。
- `cap` 的来源：有单张/部位规则时 = `rule.Size`（0＝「不缩放」时 `cap = Math.Max(w, h)` → 同样不缩但仍重编码）；
  没有规则时 = `useJpg ? maxSize : maskSize`（**PNG 类贴图吃 maskSize**，这就是"保护法线/遮罩不缩"的机制）。
- HDR 贴图更干脆：`if (cap <= 0 || Math.Max(pw, ph) <= cap)` → 直接 `未超过上限，原样保留`，
  **连重编码都不做**（只有超上限才线性空间缩放 + RGBE 重编码，且编码后更大也退回）。

### 实测（CardA.png，82 张，卡里最大 2048；上限给 4096＝比所有贴图都大）

| 跑法 | 尺寸 | 格式 | 体积 |
|---|---|---|---|
| `4096 png 90 4096` | **82/82 全部不变** | 仍 PNG（重新编码） | 65,624,725 → 49,517,405 |
| `4096 jpg 90 4096` | **82/82 全部不变** | 绝大多数变 JPEG | 65,624,725 → 11,985,659 |

逐张例子（最小的几张）：

```
TexID 106    64x64  PNG   4,408 B  →  PNG 3,212 B  →  JPEG 1,825 B
TexID  19  175x176  PNG  61,652 B  →  PNG 61,652 B（没更小→原样） → JPEG 6,236 B
TexID  33  153x130  PNG   4,763 B  →  PNG 3,880 B  →  JPEG 时保持 PNG 4,763 B（编码更大→放弃）
TexID  48   128x8   PNG     324 B  →  324 B（两边都原样，已经没得压）
```

结论一句话：**"小于上限"只决定"不缩放"，不决定"不压缩"**；
想完全不碰就选「原样不动」或别勾那个部位（那才是 `st.Fmt="keep"`）。

### 顺手修的汇总口径 bug

`贴图 N 张：JPEG a / PNG b / HDR c / 原样 d` 这一行算不平：`NKeep` 只在规则层面
"原样不动/未勾选"（`st.Fmt="keep"`）时自增，而**重编码后没更小、保留原字节**的那批
（`st.Fmt="orig"`）谁都没算 → 上面 natsu 那张卡出现 `21+37+0+0 = 58 ≠ 93`。
修法：`RepackResult` 加 `NSame`，在 `use.Length >= raw.Length` 分支自增，日志补一行：

```
贴图 93 张：JPEG 21 / PNG 37 / HDR 0 / 原样 0（q90）
  （另有 35 张按规则重编码了，但没比原字节更小 → 保留原字节，卡片不会变大）
```

（`21+37+35 = 93` ✓。这类"算不平"的日志特别容易骗到自己，凡是带分类计数的输出都要拿总数对一遍。）

---

## 29. 2.3：预设体系（卡面 PNG 预设 + 按用途分目录 + 下拉框选择）

用户原话：「修改批量压缩中的预设选项，将两个固定的预设选项改为可以选择用什么预设的预设框，加入按钮保存当前贴图设置为预设并且给当前预设命名的功能，预设保存的位置为目前的软件所在的文件夹中的preset文件夹下的batch目录。加入一个按钮打开该预设目录。在按部位压缩的保存预设的保存位置设置为preset文件夹下的coordinate目录。并且保存的预设要以原服装卡的PNG文件卡面显示并且可以被读取预设读取，预设的名字为[preset]+自定义的预设名称+原来的服装卡的名字。」

### 29.1 预设文件格式：卡面 PNG + 内嵌 JSON

新文件 `src/PresetIo.cs`：

```
┌─ PNG（原卡缩略图，到 IEND 为止）─┬─ "KoiCardTexToolPreset1" ─┬─ int32 LE 长度 ─┬─ UTF-8 JSON ─┐
```

- 好处：**资源管理器里看到的就是原卡卡面**（不用靠文件名猜），工具又能整份读回来；
  文件仍然是合法 PNG（IEND 之后的字节解码器会忽略），所以下拉框也能直接拿它当缩略图画。
- 定位载荷：`Card.PngEnd()` 之后顺序找 ASCII 标记，**再校验后面 int32 长度与剩余字节对得上**才算数
  （避免 JSON 内容里恰好出现同名字符串时误判）。
- 兼容：纯 `.json` 老预设照读（`ReadJson` 找不到标记就 `File.ReadAllText`）。
- `TexTool.PresetSave` 拆成 `PresetJson(pf)`（返回字符串）+ 写文件，`PresetLoad` 走 `PresetIo.ReadJson(path)`
  → 于是「读取预设」既能吃 .json 也能吃卡面 .png。`PresetLoadText` 里那段"没有 cardType 就用部位键反推卡种"
  的逻辑保留（第 27 节）。

JSON 是**超集信封**：既有 `Preset` 的字段（`parts`/`textures`/`uniform`/`sigs`/`cardType`/`minMatch` → `PresetLoad` 直接可用），
又有批量页要的 `plan`（类型表）+ `quality` + `protectAlpha`，还有 `presetKind`/`name`/`source`/`saved`。
一页存一页读，互不打架。

### 29.2 目录与命名

- `PresetIo.Dir(kind)` = `AppContext.BaseDirectory\preset\{batch|coordinate|chara}`
  （单文件发布下 `BaseDirectory` 就是 EXE 所在目录）。
- 文件名 = `[preset]` + 自定义名 + `_` + 原卡名（含 `.png`），非法字符替换成 `_`。
  例：`KoiCardTexTool2.3\preset\batch\[preset]部署版测试_CardA.png.png`
- 三页各归各的目录：批量→`batch`，按部位→`coordinate`，人物卡→`chara`（用户只点名了前两个，第三个顺手统一）。
- 每页都有**「打开预设目录」**按钮（`Process.Start("explorer.exe", "\"" + dir + "\"")`）。
- 首次运行 `EnsureDefaults()` 只在 `preset\batch` **空**的时候种两个内置预设
  （= 老版本那两个固定按钮的方案：「只压主贴图（已验证）」「主贴图1024+其它512」，封面是 64×64 占位图）；
  删掉不再生成。

### 29.3 第 1 页 UI 改动

- 删掉 `bPre1`/`bPre2` 两个固定预设按钮，`root` 从 7 行变 **8 行**（新插一行「预设」，
  统一设置/组过滤/按钮行/状态整体下移，`SetRowSpan(logWrap1, 8)`）。
- 新行：`预设 [cbPre1] [保存为预设…] [打开预设目录] [刷新] (状态文字)`。
  `cbPre1` 用 **OwnerDrawFixed + ItemHeight=54** 自绘：左边 48px 卡面（没有封面就画灰框），
  右边名字 + 「（原卡：xxx.png）」。第 0 项固定是「（不使用预设：按上面的类型表）」。
- 选中即套用（`PreApply`）：`PresetIo.TryReadPlan` 拿出 `plan`/`quality`/`protectAlpha`
  → `ApplyPlan()` + 质量滑块 + 保护 alpha 复选框，一行日志 + 状态文字写明来源。
- 保存：自己画的输入框 `AskName()`（不引 Microsoft.VisualBasic），
  卡面/原卡名取自 `PreCurrentCard()`（拖入或路径里的第一张；目录就取里面第一张卡）。
- 顺手：`Log1()` 补上（第 1 页原来只能 `Post("log", …)`）。

### 29.4 验证（全部走真实点击/事件路径，`uitest` 钩子）

| 场景 | 结果 |
|---|---|
| 内置种子的卡面 | `preset\batch` 自动出现两个 1 KB 预设，卡面 48×48（占位图） |
| 改过类型表 → 存 → 读回 | `SETPLAN: maintex=jpeg/512 normal=png/512 mask=auto/256 …`；存完重载并选中 → `PRESEL: maintex=jpeg/512 normal=png/512 mask=auto/256 …` **完全一致** |
| 卡面预设能否被读 | `PresetLoad OK：卡种=clothes 部位规则=0 单贴图规则=0 source=KKCoordeF_…png`，`卡面=34x48` |
| 预设真的驱动批量 | 选「只压主贴图（已验证）」跑整卡 → **49,933,282 B**，与说明书里 CLI「只压 MainTex(1024)」的参考值**一模一样** |
| 第 2 页保存 | `preset\coordinate\[preset]上衣一键方案_KKCoordeF_…png`（181,723 B）；`PresetLoad`：部位规则 **17** 条、指纹 **134** 条 |
| 第 2 页用 .png 预设走「读取预设」 | `LOADPF : …[preset]上衣一键方案_….png` → `预设已套到界面：单贴图命中 0 张，未匹配 0 张`（文件对话框过滤已含 `*.png`） |
| 第 3 页保存 | `preset\chara\[preset]本体+换装1方案_Koikatu_F_…natsu.png`（161,652 B）；`PresetLoad`：卡种 **chara**、部位规则 **157** 条、指纹 7 条 |
| 布局/截断 | 三页 `CLIP : （没有文字被截断）`；`LAYOUT` 列宽不变 |
| 衣服卡回归 | batch 10 张 `277896015 -> 41373883（14.9%）`，**10/10 SHA256 逐字节相同** |
| 发布 | `KoiCardTexTool2.3\`（SC 66,030,049 + lite 351,177 + plan + 使用说明 + 自带 preset\batch 两个内置预设）+ `kk服装卡贴图压缩2.3.zip`（60,432,660） |

存不下来时会抛出带路径和提示的 `IOException`（EXE 放在只读目录/压缩包里时的兜底说明），
不做"悄悄写到别处"的降级——那样用户在下拉框里会看不到刚存的预设，反而更难查。

---

## 30. 2.4：批量预设改纯 JSON + UI 真实截断根因（单列 TableLayoutPanel 缺 ColumnStyles）+ 预览框可上下拖

用户原话：「批量预设不需要保存为PNG文件，普通JSON文件即可，并且也加入读取预设的按钮从文件夹读取预设。同时修正UI，图1里面的文字显示不完全，UI显示不够紧凑。图2是按部位压缩区的UI图片，其中许多文字内容无法显示完全。将浏览框内的启用，贴图，等不会占用过多空间显示的内容的列距调小，整个UI窗口中最下方的空位过多。加入对预览图框占位上下调整的功能。也同时修正单张人物卡压缩区的UI。」

### 30.1 先造工具：截图 + 更严的截断自检

UI 这种事光看 `PreferredSize` 会骗人，所以这轮加了两个自检入口：

- `UiSnap(path, tab)`：`DrawToBitmap` 把整个窗口画成 PNG（uitest 里是 `f.Show()` 过的，能真画），
  命令行 `uitest <卡> snap=out.png snaptab=2` → 直接 `read_image` 用眼睛复核。
- `UiRectDump(tab)`：控件树（类型 / 位置 / 尺寸 / **PreferredSize** / Dock / 文字），`rects=1`。
- 截断自检升级：
  * GroupBox 的 `PreferredSize` 不可信（实测它返回"当前尺寸"，甚至出现 `需要 6x224`）→ 改成量**标题文字**；
  * 新增 **[切掉]** 判定：控件自己宽度够，但**被父容器切掉**（`par.ClientSize - c.Left/Top` 小于自身尺寸），
    流式布局 + AutoSize 的经典坑 —— 用户在截图里看到的"文字少了半截/多个括号"就是它，
    而旧检查（只比 PreferredSize）**完全查不出来**。

### 30.2 真凶：单列 TableLayoutPanel 没写 ColumnStyles

`new TableLayoutPanel { ColumnCount = 1, RowCount = N }` + RowStyles 而**没有 ColumnStyles** 时，
隐含列是 **AutoSize** → 列宽 = 最宽子控件的"需要宽度"。后果一串：

| 症状 | 原因 |
|---|---|
| 左列 GroupBox 宽 460 > 容器 351，标题被切 | 左列那个单列 TLP 的隐含列被内容撑到 460（`AutoSize=true` 的 GroupBox 又盖过 `Dock=Fill`） |
| 第 2 页明细表/预览框比容器宽、右边一截看不见 | `texBox` 单列 TLP 同样没 ColumnStyles，表格 PreferredSize ≈ 502 把列撑开 |
| 第 3 页"一键统一"整行被切、提示文字被压扁 | `gWrap` 单列 TLP（列 487 vs 容器 267），行高被挤到 10px → Label 上下都被裁 |

**修法**：所有单列 TLP 显式加 `ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100))`
（`outer`/`left`/`texBox`/`texBox3`/`gWrap` 共 7 处），宽度就由容器说了算。

### 30.3 GroupBox / Panel 的 AutoSize 在 Dock 组合下会虚高

第 1 页原来整页内容需要 943px，窗口只有 772 → TableLayoutPanel 压行 → 标签被上下切、最后几行（组过滤/按钮/状态）被挤掉，
正是用户说的"图1文字显示不完全"。查明两块元凶：`gb1`（输入/输出，实测 224 高而内容只有 142）
与 `pActRow`（按钮行，实测 **171** 高而内容只要 35）——`Dock=Fill + AutoSize` 互相喂尺寸，PreferredSize 退化成当前尺寸。

**修法**：`FitTab1()` 用「内容底边」写死行高（Absolute），彻底跳出反馈环：

```csharp
static int ContentBottom(Control c)   // 容器里最下面子控件的底边 + margin
{ int b = 0; foreach (Control k in c.Controls) b = Math.Max(b, k.Bottom + k.Margin.Bottom); return b; }
rootT1.RowStyles[0].Height = ContentBottom(t1Ref) + 24 + 6;   // GroupBox = 内容 + 边框/标题
rootT1.RowStyles[6].Height = max(各按钮高) + 4;               // 按钮行
rootT1.RowStyles[7].Height = ContentBottom(pStat1) + 10;      // 进度+状态
```
行 1（贴图类型表）改成 **Percent 100** 吸收余量 → 底部不再留白（类型表框变高、内容顶对齐）。
第 2/3 页左列同一套（`FitLeftColumn` + 两遍测量：改完高度子控件会重新排版，量两次才准），
并在 `tabs.SelectedIndexChanged` 里 `BeginInvoke` 再 fit 一次（子控件要到切页后才真正定位好）。

### 30.4 表格列宽：Fill + MinimumWidth

原来 `AutoSizeColumnsMode.None` + `FillWeight` —— FillWeight **被完全忽略**，每列都是默认 100px，
窄面板下必然横向滚动、列间大段留白。改成 `Fill` + 每列 `MinimumWidth`：
窗口宽时按 FillWeight 铺满，窄时压到最小宽度再出横向滚动条。
列名也顺手缩短（贴图→张、贴图大小→大小、材质名→材质、处理方式→方式、最大边→边长），
「启用」这类窄列最小宽 42px 保证标题不被切。列宽整体调窄 → 宽度让给「部位 / 组·部位」。
日志 `RichTextBox` 改 `WordWrap=true` + 只留竖向滚动条（长行不再被切、也不用拖横条）。

### 30.5 预览框可以上下拖

新增 `MakeRowSplitter(t, splitterRow, topRow, bottomRow, minTop, minBottom)`（`Cursors.HSplit`，
按下时记一次起始行高、之后按位移改两行——和左右分隔条同一套防"越拖越飞"逻辑）。
第 2/3 页的明细框与预览框之间各放一条：默认预览 210px，预览最低 70、明细最少留 140。
自检钩子 `UiDragRowSplitter(which, dy)` / `UiPreviewHeight(which)`。

### 30.6 批量预设改纯 JSON + 「读取预设…」

- 保存：`preset\batch\[preset]名字_原卡名.json`（UTF-8 无 BOM），内容仍是超集信封
  （`presetKind/name/source/quality/protectAlpha/plan` + 给 `PresetLoad` 用的 parts/textures/sigs）。
- 新增 **「读取预设…」** 按钮（`PreLoadDialog`，默认开在 `preset\batch\`）→ 套用类型表+质量+保护 alpha。
- 下拉框不再需要卡面：去掉 OwnerDraw，`PresetIo.List` 对 `.json` 也读字段（显示名/原卡/时间）。
- 按部位页 / 人物卡页**保持**卡面 PNG 预设（用户只要求批量改 JSON）。
- `PresetIo.FileName(custom, card, ext)` 加扩展名参数；`EnsureDefaults()` 种的两个内置预设也改成 .json。

### 30.7 验证

| 项 | 结果 |
|---|---|
| 三页截断自检 | 第 1/2/3 页全部 **「（没有文字被截断）」**（含新的 [切掉] 判定）——修前第 2 页有 2 条 [切掉] + 2 条 [截断]，第 3 页 `一键统一` 行被切 |
| 截图复核 | `_work\tex\ui\t1z.png` / `t2z.png` / `t3z.png`（1376×879）逐张看过：类型表 9 行全见、按钮行/状态在最底、部位表列宽正常、预览框有图 |
| 批量预设往返 | 存 `[preset]UI回归_….json`（791 B）→ 下拉框列出（显示名 + 原卡）→ 选中套用：`maintex=jpeg/512 mask=png/256` 与保存前**逐字段一致** |
| 预览框拖动 | 初始 204 → 往上拖 -60 → 行高 264（实际 270）→ 往下拖 +100 → 行高 170（实际 176）；第 3 页同样生效 |
| 第 2 页 GUI 压缩（只勾上衣） | 62.58 MB → 59.21 MB，`完成：成功 1 张` |
| 第 3 页 GUI 压缩（只勾本体） | 270.85 MB → 270.77 MB，产物 **283,922,364 B**（与 2.1/2.3 参考值一致） |
| 衣服卡 batch 回归 | `277896015 -> 41373883（14.9%）`，**10/10 SHA256 逐字节相同** |
| 发布 | `KoiCardTexTool2.4\`（SC 66,032,658 + lite 358,345 + plan + 使用说明）+ `kk服装卡贴图压缩2.4.zip`（60,435,424） |

经验一句话：**WinForms 里凡是"AutoSize / Dock 混着用"的容器，别信 PreferredSize，直接用
`DrawToBitmap` 截图 + 「子控件是否超出父容器」这两把尺子量**——这轮 4 个"文字显示不全"的表象，
根因只有两条（单列 TLP 的 AutoSize 列、GroupBox/Panel 的 AutoSize 虚高）。

---

## 31. 2.5：底部那条"怎么缩都不消失"的空白

用户原话：「查看图1，2。UI下方存在大量的空白区域，通过缩放窗口无法减小这个区域，同时会挤占上方的空间，
移除这个空白区域。把部位一览和选中部位的贴图明细中的括号内容全部删掉。把独享这两个字改成独占贴图。
同时也把这些改动应用到单张人物压缩界面中。」

### 31.1 定位（`UiRectDump` 一击命中）

第 2 页实测：`TableLayoutPanel @7,672 1326x89` 是底部动作条 —— **89px 高，里面内容只要 33px**。
也就是说窗口最底下 56px 是动作条自己虚高出来的空白；再加上左列（内容 590 / 容器 653）剩下的 ~57px，
用户看到的就是「底部一大片空白 + 上面的表格被挤」。

两头都是同一类毛病：
- `actRow`（动作条）又是 `TableLayoutPanel{ AutoSize=true, Dock=Fill }` → PreferredSize 退化成当前尺寸；
  而且**只改控件高度没用**（它所在的 AutoSize 行还会去问 PreferredSize）→ 必须像第 1 页那样
  **把外层那一行也写死**（`FitActionRow` 里取 `actRow.Parent` 的 `GetRow()` 改 RowStyle）。
- 左列的空档：内容比容器矮，多出来的就是白条。

### 31.2 修法

1. `FitActionRow(actRow2/actRow3)`：按子控件 PreferredSize 算高（≥34），**同时把 `outer` 的那一行
   设成 Absolute**。实测 89 → **43px**，`t` 从 659 → **705px**（表格把空间全吃回来了，
   即用户说的"挤占上方空间"没了）。
2. 左列多出来的地方**改成有用的东西**：把状态文字 `lblP2Status` / `lblG3Status` 从左列底部的
   「空白」变成 `Panel{Dock=Fill}` + `Label{Dock=Fill, AutoSize=false, TextAlign=TopLeft}`
   （自动换行、顶对齐），并把它从动作条里摘掉（动作条只剩按钮，也更短）。
   第 2/3 页的左列三行 = [选卡][输出/选项][状态文字]，不再有白带。
3. 顺手把「AutoSize=false 的 Label 是故意换行的」写进自检规则：**状态条/预览提示这种标签不该按
   单行宽度判 `[截断]`**（否则新状态条会误报）。

### 31.3 文案/表头

- 「2) 部位一览（勾选＝要压；自动＝安全路线）」→「**2) 部位一览**」
- 「3) 选中部位的贴图明细（默认「跟随部位」）」→「**3) 选中部位的贴图明细**」
- 第 3 页同理：「2) 角色本体 / 换装 × 部位（勾选＝要压）」→「2) 角色本体 / 换装 × 部位」、
  「3) 选中组·部位的贴图（可单独指定）」→「3) 选中组·部位的贴图」
- 两页的「独享」列 → **「独占贴图」**（MinimumWidth 48 → 72，标题不被切）

### 31.4 验证

| 项 | 结果 |
|---|---|
| 动作条高度 | 第 2/3 页 89 → **43px**；`t` 659 → 705（表格变高） |
| 左列底部 | 空白 → 状态文字区（第 2 页显示「已读取 82 张贴图，17 个部位…」自动换行、第 3 页显示「已读取：93 张贴图 / 157 个「组·部位」条目」） |
| 截图复核 | `_work\tex\ui\w1/w2/w3.png`（改后）与 `v t1/t2/t3.png`（部署版）逐张看过：三页底部都贴到窗口边，没有白带 |
| 截断自检 | 三页全绿（含新的「AutoSize=false 不判截断」规则） |
| 衣服卡 batch 回归 | `277896015 -> 41373883（14.9%）`，**10/10 SHA256 逐字节相同** |
| 第 2 页 GUI（只勾上衣） | 62.58 MB → 59.21 MB，成功 1 张 |
| 第 3 页 GUI（只勾本体） | 270.85 MB → 270.77 MB，成功 1 张 |
| 发布 | `KoiCardTexTool2.5\`（SC 66,032,921 + lite 358,857 + plan + 使用说明）+ `kk服装卡贴图压缩2.5.zip`（60,435,783） |

> 注：用户当时开着 2.4（exe 被占用），所以这版直接发成 **2.5** 新目录，不去动被锁的文件。

---

## 32. 2.6：「现在的压缩算法是什么？有没有更高效的？」——查证 + 落地无损 PNG 再压缩

用户原话：「目前的图片压缩算法是什么，有没有存在更加高效的算法」

### 32.1 先查证现状（代码事实）

| 用途 | 实现 | 关键限制 |
|---|---|---|
| PNG | GDI+ `Bitmap.Save(ms, ImageFormat.Png)` | **压缩级别不可设**、滤波策略固定 |
| JPEG | GDI+ 编码器 + `Encoder.Quality` | 基线、标准哈夫曼表、固定色度抽样 |
| 灰度 | `gray && flatAlpha` → 8bpp 调色板 PNG | 已有亮点（掩罩/法线省很多） |
| HDR | 手写 RGBE（`Hdr.cs`） | 线性空间盒式缩放 |
| 缩放 | GDI+ `HighQualityBicubic` | — |
| 兜底 | 重编码后不更小 → 原样透传 | 卡片永不变大 |

### 32.2 测量（真卡、等质量、可复现）

写了两支探针：`_work\tex\enc_cmp*.py`（Pillow 参照）+ 工具内 `pngtest` / `pngre` 子命令。

**PNG（同像素同通道重编码）**：Pillow(zlib 9 + 自适应滤波) 比 GDI+ 逐张小 3%~45%；
工具自写编码器在 15 张样本上 **GDI+ 17,976,011 → 13,436,829（−25.3%）**，其中：
- 纯换编码器（RGB/RGBA）≈ **−16%**
- 把「灰度+alpha」从 RGBA(4 通道) 降到 **LA(2 通道)** 再省一大截（GDI+ 根本写不出 2 通道 PNG）→ 合计 −25%
  （用户这轮只选了"无损 RGB/RGBA"，LA 留作以后的可选项）

**JPEG（PSNR 对齐质量）**：GDI+ q90 与 Pillow/libjpeg-turbo q90 总量差 **−1.1%**、逐张 ±5%
→ **换 libjpeg-turbo 没有意义**；要再省 10~20% 得上 mozjpeg/jpegli（原生 DLL，破坏单文件）。
（注：第一版对比脚本拿"全通道 JPEG"去比工具"只对不透明贴图转 JPEG"的产物，得出 −30% 的假结论；
改成只比两边都是 JPEG、且原图 alpha 全不透明的贴图、并用 PSNR 对齐质量后才是真结论。）

**格式**：WebP 无损实测只要原卡的 44%（PNG 75%），但 Unity 5.6 的 `LoadImage` 不认 → 不能用。

### 32.3 踩到的坑（很关键，差点发了有损的版本）

第一版是"自己从 Bitmap 读像素再编 PNG"（`PngEnc.EncodeColor`）。回归一比对发现
**13 张贴图的像素变了**（半透明像素 RGB 差 ±1~3，alpha 一致）。查证结论：
**GDI+ 内部是预乘 alpha 缓冲区**，`LockBits(Format32bppArgb)` 反预乘时有取整误差 —— 那就不是无损了。

于是改成 **`PngEnc.Recompress`：只对 GDI+ 已经写好的 PNG 做"重新滤波 + 重新 deflate"**：
- 解析 IHDR/IDAT（隔行 / 非 8bit 直接放弃），inflate 出**已滤波的扫描线**
- 反滤波成原始字节 → 重新按"绝对值之和最小"选 5 种滤波 → zlib-ng 重新压
- 颜色类型、位深、PLTE/tRNS 等块全部原样搬运，**完全不解释像素** → 天然无损
- 单文件自检：`pngre <in.png> <out.png>`（真实贴图 278,639 → 186,135 = −33.2%，解码后像素完全相同）

**第二个坑**：把 `pngNew` 变小之后，"不更小就原样透传"的判据被翻转了 —— 有 13 张原本**原样透传**
的贴图（作者原图带 `DATx`/`zxuS` 垃圾块、没有 gAMA）现在被重编码了，像素因此变了 ±1~3。
定稿改成：**只在"本次本来就是用我们重编码出来的 PNG"时才替换**（`ReferenceEquals(use, pngNew)`），
也就是它**只改字节数、绝不改变"哪些贴图会被重编码"**。

### 32.4 落地与实测

- 新文件 `src/PngEnc.cs`（`Recompress` + 备用 `Encode`/`EncodeColor`）；`TexTool.Repack` 里在
  `use` 决定之后替换；`RepackResult` 加 `PngPacked/PngSaved`，日志多报一行。
- 压缩档：默认 `Optimal`（实测拿到 85% 收益、只要 60% 额外耗时）；
  「PNG 极限压缩」勾选框 / `pngmax=1` / `KOITEX_PNG_FAST=1` 可切换。

| 场景 | 旧（2.5） | 新（2.6 默认档） | 差异 |
|---|---|---|---|
| 衣服卡 CardA.png…917（82 张） | 20,556,013 | **19,963,872** | −2.9%（29 张 PNG 参与，省 579 KB） |
| 人物卡 natsu（93 张） | 22,843,999 | **21,382,668** | −6.4%（33 张 PNG，省 1.39 MB） |
| 10 张样本 batch 载荷 | 37,322,755 | **33,870,429** | −9.25% |
| 同一张衣服卡耗时 | 5.6 s | 10.3 s（Optimal）/ 17.4 s（SmallestSize） | 慢约 1.8× / 3.1× |

**无损证明**：`_work\tex\lossless_check.py` 逐 TexID 解码比对 ——
10 张样本卡 + 人物卡全部 **"有问题的: 0"**（每张要么字节完全相同，要么解码后像素完全相同）。

| 其他验证 | 结果 |
|---|---|
| 三页截断自检 | 全绿（第 1 页新增勾选框后仍 `（没有文字被截断）`） |
| 卡片结构校验 | 10/10 `PASS`，失败 0 |
| 发布 | `KoiCardTexTool2.6\`（SC 66,036,685 + lite 366,025 + plan + 使用说明）+ `kk服装卡贴图压缩2.6.zip`（60,441,253） |

> 留下的诊断命令：`pngtest <卡> <TexID,…>`（GDI+ vs 自写编码器逐张对比）、
> `pngre <in.png> <out.png>`（无损再压缩单文件自检）。

**结论一句话**：PNG 侧"更好的算法"确实存在且**无损**（换滤波 + 换 deflate 实现，整卡 −3%~9%）；
JPEG 侧 GDI+ 已经和 libjpeg-turbo 打平，想再省只能引原生 mozjpeg/jpegli；
而真正的体积大头永远是"该不该缩分辨率 / 该不该转 JPEG"，编码器只是收尾。

---

## 33. 2.7：「灰度+alpha → 2 通道 LA」实验项 + 测试卡

用户原话：「先用1给我做个卡测试」（= 上一轮列的方案 ①：灰度+alpha 降成 2 通道）

### 33.1 实现

`PngEnc.Recompress(png, allowGrayAlpha, out kind)` 多加一步：解出扫描线之后，
若颜色类型是 6(RGBA) 或 2(RGB)，**逐像素检查 R==G==B**，成立才：

| 源 | 转换后 | 通道 |
|---|---|---|
| RGBA 且每像素 R==G==B | 灰度+alpha（colorType 4） | 4 → **2** |
| RGB 且每像素 R==G==B | 灰度（colorType 0） | 3 → **1** |

- 只搬 R(=G=B) 与 A，**数值一个 bit 不改**；不成立就原路返回 null。
- 另外：降通道后 `tRNS` 块不合法（只对 0/2/3 号类型有效）→ 转换时丢掉它。
- 与"只改字节数"的原则一致：仍然只在**本次本来就要重编码的 PNG** 上做，且结果更小才采用
  （`ReferenceEquals(use, pngNew)` 之后才替换）。
- 开关：GUI 第 1 页「灰度用 2 通道（实验）」/ CLI `pnga=1` / `PngEnc.UseGrayAlpha`。

### 33.2 顺手补的 CLI 缺口

`compress` 分支原来**根本没解析 `plan=`**（一直 `Repack(..., null, ...)`），
只有 `batch` 支持 —— 于是"用计划 JSON 跑单张卡"一直是在用默认类型表，静默走错路。
现在 `compress` 也会读 `plan=<file.json>`（`PlanIo.Load`）。

### 33.3 验证（逐像素无损）

`lossless_check.py` 已升级：允许"通道类型变少"（RGBA→LA/RGB→灰度），
把两边都展开成 RGBA 再比像素，通道变了但像素相同记为"无损替换"。

| 场景 | 结果 |
|---|---|
| 衣服卡（法线/遮罩/高度/其它 = PNG/0，不缩放） | 18 张转 LA/L，**0 像素差异**；19,963,872 → **18,798,265**（−5.8%） |
| 衣服卡小卡（上+遮罩类 PNG/1024） | 17 张，**0 差异**；24,375,995 → **23,249,766**（−4.6%） |
| 人物卡小卡 | 9 张，**0 差异**；25,978,518 → **24,605,617**（−5.3%） |
| 人物卡（不缩放只重编码遮罩类） | 5 张，0 差异；248,393,099 → 221,393,370（**−27 MB**，那些 2048² 的 body/eye 遮罩最赚） |
| 默认路径（LA 关，10 张样本） | 载荷 −9.25%，**0 像素差异**（与 2.6 行为一致） |
| 部署版可复现 | 2.7 的 exe 跑同参数 → 与测试卡 **SHA256 一致** |
| 三页截断自检 | 全绿（第 1 页多两个勾选框后仍 `（没有文字被截断）`） |

### 33.4 测试卡产物

`LA测试\`（附 `测试说明.txt`、两个计划 JSON）：
- `LA测试_衣服卡_小.png` / `_对照_衣服卡_小_无LA.png`
- `LA测试_人物卡_小.png` / `_对照_人物卡_小_无LA.png`
- `LA测试_衣服卡.png` / `_对照_衣服卡_无LA.png`（严格全无损版，含"不缩放"）

**还没验证的只有一件事**：Unity 5.6 认不认 colorType 4（gray+alpha）PNG。
所以给用户的测试方式是**同款卡的有 LA / 无 LA 两份**：对照卡坏→是遮罩重编码的问题；
只有 LA 卡坏→就是 2 通道不被支持。不能用就取消勾（`pnga`），2.6 的纯再压缩不受影响。

发布：`KoiCardTexTool2.7\`（SC 66,037,330 + lite 367,561 + plan + 使用说明）+ `kk服装卡贴图压缩2.7.zip`（60,442,609）

---

## 34. 2.8：mozjpeg/jpegli 考察 → 落地「纯托管无损 JPEG 优化器」+ 可选外部编码器

用户原话：「测试后发现没有问题。接下来尝试 mozjpeg/jpegli 原生 DLL」（"测试没问题"= 2.7 的灰度+alpha 2 通道 PNG 游戏里加载正常）

### 34.1 沙箱网络：HTTPS 被拦、HTTP 通 —— 靠 HTTP 镜像拿包

- 直连 `https://github.com` 等一律 `HTTP=000`（0.02s 秒拒）；`http://github.com` 却 `301`、`http://neverssl.com` `200` → **只有 443 被拦**。
- WinHTTP/IE 都没配代理、本机也没有常见代理端口在听；`Resolve-DnsName` 正常（DNS 通）。
- **`http://mirrors.aliyun.com/pypi/simple/` 200** → 从 HTTP 镜像取 PyPI 包：
  - `pyjpegli`（cp313-win_amd64.whl，409 KB）= jpegli 的 Python 绑定
  - `mozjpeg-lossless-optimization`（cp313 wheel，60 KB）= mozjpeg 的 jpegtran 等价物
- `pip install` 装不进去（沙箱拦它的临时目录）→ 直接 `ZipFile` 解 wheel（`.data/platlib/` 里的东西要落到根目录），
  设 `PYTHONPATH` 即可 `import`。

### 34.2 两个包的 P/Invoke 路走不通

`pe_exports.py`（自己手写的 PE 导出表解析）显示两个 `.pyd` **只导出 `PyInit_*`** ——
cffi/pybind11 把 C 符号静态链接进去了，没有导出 → 不能 `LoadLibrary` + P/Invoke。
（要用就只能把 Python 当外部进程，见 34.4。）

### 34.3 实测收益（真卡、等质量）

| 项 | 结果 |
|---|---|
| jpegli **等 PSNR** | 比 GDI+ 小 **−6.9%**（逐张 −0.9% ~ −54.8%） |
| jpegli **同一个质量数字 q90** | 反而 **+15.9%**，但 PSNR **更高** → 两者质量语义不同（GDI+ 90 ≈ jpegli 80~85） |
| mozjpeg 无损优化（作用在 GDI+ 产物上） | **−8.6%**，且解码像素完全相同 |
| ↑ 但它的产物是 **渐进式 SOF2** | 所以这 −8.6% 里混着"渐进式"的功劳；**基线最优哈夫曼本身只有 3~4%** |

→ 结论：**mozjpeg 的主要增量在 progressive**；单纯"最优哈夫曼"我们用纯托管就能做到。

### 34.4 落地：`src/JpegOpt.cs`（纯托管无损 JPEG 再优化）

把 libjpeg 的 `jpeg_gen_optimal_table` 那套算法整段移植成 C#：
基线解析（SOF0/SOF1、8bit、无 restart）→ 熵解码出量化系数并统计符号频率 →
按频率生成**最优哈夫曼表**（含 16 位长度限制）→ 用新表重编码 → 原样拼回（DQT/SOF0/APP0 保留，COM/APPn 丢掉）。
**系数一个都不动 → 像素逐位不变**；只在更小时替换，任何不确定一律返回 null。

调试中踩到并修掉的 4 个坑（都记在这里，免得重来）：

| # | 坑 | 症状 |
|---|---|---|
| 1 | SOS 里分量选择器偏移少算 1（`Ns` 那一字节） | 所有文件都判"分量 ID 对不上" |
| 2 | `MinCode/MaxCode` 用 `short` | 16 位码字 > 32767 溢出成负数 → 被当成"该长度没码字" → 解码错 |
| 3 | 漏了 libjpeg 的"去掉伪符号 256"（`while(bits[i]==0)i--; bits[i]--;`） | `bits[]` 比实际写出的符号多 1，码表错位 |
| 4 | 收符号时扫 `1..16`（应扫 `1..MAX_CLEN`） | 长度限制只改 `bits[]` 分布、`codesize[]` 仍是原长 → 超长符号整批丢失（实测漏 0x36） |

实测：单张贴图 217,244 → 210,202（**−3.2%**，像素完全相同）；一张衣服卡 20 张 JPEG 共省 **52.5 KB**；
10 张样本卡批量省 **64 KB**；全卡 19,963,872 → 18,744,525（−6.1%，其中 LA 1.60 MB + JPEG 52 KB）。

### 34.5 可选外部编码器：`src/ExtJpeg.cs`

`EXE 同级 mozjpeg\` 里放 `cmd.txt`（模板，占位符 `{in} {out} {q}`）或用内置模板（`cjpegli.exe` / `cjpeg.exe` …），
工具在 JPEG 编码后把 PNG 交给它、**更小才采用**，失败/超时/产物不是 JPEG 一律静默回落 GDI+。
端到端实测（Python + pyjpegli 当外部编码器）：20 张里 15 张被替换，**再省 66.6 KB**，且这些张 PSNR 不低于 GDI+。

### 34.6 验证与发布

| 项 | 结果 |
|---|---|
| 无损性（逐 TexID 解码比对） | 10 张样本卡 + 衣服卡全部 **"有问题的: 0"**（字节相同或像素完全相同） |
| 2.1 基线对比 | 载荷 37,322,755 → 33,213,792（**−11.01%**） |
| 耗时 | 10 张 30s → 37s（+23%） |
| 部署版可复现 | 同参数产物与开发版**逐字节相同** |
| 三页截断自检 | 全绿 |
| 发布 | `KoiCardTexTool2.8\`（SC 66,044,604 + lite 381,897 + plan + 使用说明 + `mozjpeg\`）+ `kk服装卡贴图压缩2.8.zip`（60,451,459） |

**仍未做的（下一步可选）**：自己实现**渐进式 JPEG**（谱选择 + 逐次逼近），
才能吃掉 mozjpeg 那 −8.6% 里剩下的 ~5%；因为会改变 JPEG 编码类型，需要一次游戏内加载验证。

---

## 35. 2.9：自己实现渐进式 JPEG（谱选择）——追平 mozjpeg 的 −9.5%

用户原话：「再试试看」（= 上一轮结尾留的那句"要不要自己实现渐进式"）

### 35.1 实现（`src/JpegOpt.cs` 续）

在已有"基线最优哈夫曼"之外加一条路：**渐进式多扫描（只做谱选择，不做逐次逼近）**。

扫描脚本（3 分量；单分量时亮度那两段）：

| # | 分量 | Ss..Se | 说明 |
|---|---|---|---|
| 1 | 全部 3 个 | 0..0 | DC（交错 MCU，共用一张 DC 表） |
| 2-5 | Y | 1-2 / 3-5 / 6-20 / 21-63 | 亮度按频段拆 4 段 |
| 6-7 | Cb | 1-5 / 6-63 | 色度各拆 2 段 |
| 8-9 | Cr | 1-5 / 6-63 | 同上 |

每个扫描：先用一次"只统计符号频率"的假编码算出**这一扫描专属的最优哈夫曼表**，
再真正写位；每个扫描自己带 DHT+SOS。AC 扫描是单分量、非交错（一个 MCU = 一个块），
用 **EOB 游程**压缩连续的全零块。

### 35.2 三个 bug（都靠"换一个解码器当裁判"才定位）

| # | bug | 症状 / 发现方式 |
|---|---|---|
| 1 | AC 扫描头写了 `Ta=1`，但表是以 0 号写进去的 | Pillow 直接 `broken data stream` |
| 2 | **EOB 游程没在"下一块有系数"时先冲刷** | 16×16 小图能过、真贴图全块错位；手工逐位解码自己的码流证明"编码是对的"，才把矛头转向解码器 |
| 3 | （不是 bug）**Pillow 在 `draft('YCbCr')` + 渐进式下会解错** | 我一度以为编码错；换 **ffmpeg** 当第二裁判，ffmpeg 解出来 Y 块均值 73/125/40/178 正好是 DC-only 应有的平块 → 编码其实没问题 |

教训写进流程：**无损性验证必须至少两个独立解码器**（本项目现在的做法：ffmpeg + libjpeg/Pillow，
都用 `-i x.jpg out.png` 或 `Image.open().load()` 之后逐像素比）。

### 35.3 收益（真贴图、双解码器验证像素完全相同）

| 方案 | 结果 |
|---|---|
| 基线（最优哈夫曼） | 229,160 → 210,202（−3.2%） |
| 渐进式，频段只拆 1-5/6-63 | 229,160 → 215,338（**−6.0%**） |
| 渐进式，频段细分 4 段 + 色度各 2 段 | 229,160 → 208,168（**−9.2%**） |
| mozjpeg 参考（`mozjpeg_lossless_optimization`，产物 SOF2） | 229,160 → 207,385（−9.5%） |

→ 结论：**渐进式的收益主要来自"每扫描专属最优表 + 更窄的符号分布"，而不是必须做逐次逼近**
（mozjpeg 用 10 扫描含逐次逼近，我们用 9 扫描纯谱选择，差距只剩 0.3 个点）。
`KOITEX_PROG_DBG=N` 可以只发前 N 个扫描用于二分定位。

卡级收益就小多了（工具逐张挑"基线 vs 渐进式"更小的那个）：
衣服卡 18,744,525 → **18,693,815（−50.7 KB）**；测试计划下只有 5~11 张贴图走 JPEG，收益 13~22 KB。

### 35.4 默认关 + 测试卡（因为"能不能被游戏加载"没验证）

渐进式 JPEG 是标准格式，但**不是所有解码器都支持**：Unity 5.6 的 `Texture2D.LoadImage`
历史上用 `jpgd`（不支持渐进式）。所以：

- `JpegOpt.Progressive` **默认 false**；GUI 勾选框「JPEG 用渐进式」/ 命令行 `jpegprog=1`。
- 产出 `渐进式测试\`：`渐进式测试_衣服卡.png` / `_对照_衣服卡_无渐进式.png`、
  `渐进式测试_人物卡.png` / `_对照_人物卡_无渐进式.png`（**只有渐进式开关不同**，其余设置一致）
  + `测试说明.txt`（写清"看卡能不能读出来"，以及失败就取消勾选、其余优化不受影响）。

### 35.5 验证与发布

| 项 | 结果 |
|---|---|
| 10 张样本 batch（开渐进式） | 载荷 37,322,755 → 33,193,518（**−11.06%**），逐 TexID 比对 **0 像素差异** |
| 单张贴图（真贴图 + 小图） | ffmpeg 与 libjpeg 双解码器都判定**像素完全相同** |
| 三页截断自检 | 全绿（第 1 页多到 4 个勾选框后仍 `（没有文字被截断）`） |
| 耗时 | 10 张样本 32s（与不开渐进式基本持平） |
| 发布 | `KoiCardTexTool2.9\`（SC 66,046,124 + lite 385,481 + plan + 使用说明 + `mozjpeg\`）+ `kk服装卡贴图压缩2.9.zip`（60,453,459） |

---

## 36. 2.10：按用户要求删掉「渐进式 JPEG」

用户原话：「把渐进式 JPEG这个功能删了吧，对于压缩的贡献太小了」

评价确实如此——**卡级收益只有 0.1%~0.3%**（2.9 实测：衣服卡 −50.7 KB / 19 MB，
测试计划下 −13~22 KB），却背着"游戏可能加载不了"的风险（Unity 5.6 的 `LoadImage`
历史上用 jpgd，不认渐进式）。性价比不足，删。

删除内容（一次清干净，`grep Progressive|jpegprog|EncodeScan|EobRun` 已归零）：

| 位置 | 删掉的东西 |
|---|---|
| `src/JpegOpt.cs` | `Progressive` 开关、`UsedProg/UsedBase`、`TryProgressive`、`BuildScans`、`Scan` 类、`EncodeScan`、`EmitEobRun`、`WriteScanHeader`、`KOITEX_PROG_DBG` 调试开关 |
| `src/MainForm.cs` | 第 1 页「JPEG 用渐进式」勾选框 |
| `src/Program.cs` | `jpegprog=0/1` 命令行开关 |
| 交付物 | `KoiCardTexTool2.9\` + `kk服装卡贴图压缩2.9.zip` + `渐进式测试\`（测试卡与说明） |

保留下来的是与渐进式共用的、**真正有用**的那部分（在 2.8 里已经存在的基线路径）：
最优哈夫曼表生成（`BuildOptimal`，含 libjpeg 的伪符号处理与 16 位长度限制）、
`AssembleBase`、`BitWriter`。也就是说删掉的是"多扫描编排"，不是"更好的哈夫曼"。

### 验证

| 项 | 结果 |
|---|---|
| 单卡产物 | 18,744,525 字节 —— **与 2.8（无渐进式）逐字节一致** |
| 10 张样本 batch | 载荷 −11.01%，逐 TexID 比对 **0 像素差异** |
| 部署版可复现 | 与开发版 **SHA256 一致** |
| 三页截断自检 | 全绿（勾选框回到 3 个，行不再拥挤） |
| 发布 | `KoiCardTexTool2.10\`（SC 66,044,692 + lite 382,409 + plan + 使用说明 + `mozjpeg\`）+ `kk服装卡贴图压缩2.10.zip`（60,451,841） |

使用说明里那一段改成了"2.9 曾试过、2.10 已删除，原因是收益 0.1~0.3%、且存在兼容风险"，
免得以后有人翻到 2.9 的说明以为现在的版本还有这个功能。

---

## 37. 2.11：细节图降采样改「面积平均 + 低分辨率域轻锐化」并设为默认

用户拿 2.10 的同分辨率（1024²）测试卡进游戏实测后确认：**「新方案细节更好」**。

### 37.1 为什么换

工具一直用 GDI+ `HighQualityBicubic` 降采样。离线实测（见 4 组预研，`_work\resample\H1-H2预研报告.md`、`H4-H5预研报告.md` 及其附录 A）：

| 口径 | 双三次（旧） | 面积平均 | 面积平均+锐化 0.6 |
|---|---|---|---|
| 细节图对比度保持（4096→1024 真图） | 22.8% / 7.4% | 24.8% / 14.5% | **31.8% / 16.0%** |
| SSIM | 0.492 / 0.323 | 0.492 / 0.320 | **0.512 / 0.327** |
| 光照误差（当细节法线用） | 3.71 | 3.45 | **3.24** |

原因：**双三次的负瓣会抵消一部分高频**——它更"平滑"、文件更小，但细节也更少。
面积平均真的把 4×4（或 2×2）个像素平均掉，保住更多高频；
再在**低分辨率域**（存进卡里的那张小图上）补一点锐度，能把被模糊吃掉的结构对比拉回来
（放大后再锐化只会放大双线性插值的伪影，是错的做法）。

### 37.2 实现（`src/Resample.cs`，新文件）

- `Area(src,w,h)`：整数倍时的精确块平均（LockBits + 4 通道整数累加，round-half-up）。
  非整数倍、或没在缩小时返回 null → 调用方退回 GDI+ 双三次。
- `UnsharpInPlace(bmp,k)`：3×3 `[1 2 1]⊗[1 2 1]/16` 高斯，`RGB += k*(RGB-blur)`，**alpha 不动**
  （alpha 是覆盖率，锐化会让掩罩边缘长光晕）。
- `IsDataClass(cls)`：只对 `normal` / `mask` / `height` 生效。**彩色主贴图仍走双三次**（未测，不一起换）。
  注意工具把 `BumpMap` 归入 normal 类（`TexTool.cs:71`），所以彩色 bump 图也会走新路径——
  机制相同（都是数据量），但预研样本是灰度细节图，这一点在文档里如实标注。
- 开关：`Resample.DetailMode`（**默认 true**）、`Resample.Sharpen = 0.6`；
  命令行 `kernel=bicubic` 关、`sharpen=` 调强度；GUI 第 1 页勾选框「细节图保细节」（带 tooltip）。

### 37.3 实测（产物卡 vs 原卡逐张对比，"产物放大回原尺寸再比"）

**人物卡**（只看 2048² 以上、真被降采样的 10 张）

| 指标 | 2.10 现状 | 2.11 新方案 | 变化 |
|---|---|---|---|
| 对比度保持 | 26.4% | **32.4%** | +23% |
| 高频能量比 | 0.18 | 0.21 | +17% |
| SSIM | 0.564 | **0.574** | +2% |
| 光照误差 | 11.75 | **9.61** | **−18%** |
| 细节图字节 | 9.00 MB | 9.51 MB | +0.51 MB |
| 整卡 | 24,540,807 | 25,026,723 | **+2.0%** |

逐张（对比度保持 / 光照误差）：30 `6.7%→17.9%` / `13.25→8.05`；34 `17.8→26.3%` / `6.63→5.64`；
35 `11.1→16.4%` / `15.25→10.69`；45 `20.9→29.3%` / `10.73→7.94`；55 `20.9→26.3%` / `7.81→4.76`；
26 `42.0→53.0%` / `12.08→10.36`；77 `54.7→65.2%` / `20.11→18.95`。

**衣服卡**（被降采样的 18 张）：对比度保持 6.8%→7.1%、SSIM 0.147→0.155、整卡 +0.9%。
提升小的原因：这些图的细节本来就是纯高频织物颗粒、只降一半，损失来自**分辨率**而非滤波器。

### 37.4 验证

| 项 | 结果 |
|---|---|
| 差异范围 | 10 张样本卡逐 TexID 比对：**只有 mask/normal（含 BumpMap）/height 变化**，maintex·emission·reflection·matcap·highcolor **全部逐字节不变** |
| 结构校验 | 每张卡 `[OK] 校验通过: N 张贴图可解码` |
| 开关回归 | `kernel=bicubic` 退回旧行为（人物卡 19,899,665 字节，与 2.10 口径一致） |
| 10 张样本批量 | 37,264,920（旧核）→ 38,758,612（新方案），**+4.0%**，10/10 成功 |
| 三页文字截断自检 | 全绿（第 1 页勾选框已到 6 个，仍无截断） |
| 发布 | `KoiCardTexTool2.11\` + `kk服装卡贴图压缩2.11.zip` |

### 37.5 教训（写给以后的自己）

- **"分辨率不变"也能买到细节**：换核 + 轻锐化是**同分辨率**下的净改善，成本只有 +2% 体积；
  比"提高分辨率（+100% 体积）"划算一个数量级。之前几轮一直在算"降多少分辨率"，漏掉了"怎么降"。
- **指标口径要先想清楚**：SSIM/RMSE 天然偏向"越模糊越像"，用它评"细节丢了多少"会得出反向结论；
  必须同时看局部对比度/频带能量这类"强度口径"。
- **合成用例容易骗自己**：本轮对比度补偿、噪声再注入两个方向都是先在合成图上"看起来有效"，
  换成真实贴图 + 正确口径后立刻翻车（方块伪影、SSIM 崩塌）。

---

## 38. 2.12：预设系统改造（仅优化 / 质量 / 性能）+ 三个"无损其实有损"的修复

### 38.1 用户要求

「保留 N 预设、B 预设作为基础预设并分别改名为 质量、性能；加入新预设，全部设置为 auto + 无损优化作为默认预设，预设名为 仅优化」。

### 38.2 三个内置基础预设（`PresetIo.EnsureDefaults`）

| 预设 | 计划 | 实测（14 张 example 服装卡，1,077,229,777 字节） |
|---|---|---|
| **仅优化**（默认） | 9 个类全部 `auto/0`（0 = 原尺寸，**不降分辨率**） | → 708,096,371（**−34.28%**） |
| **质量** | maintex `auto/2048`，其余 `auto/1024` | → 165.9 MB（**−83.85%**，精度代价 20.6%） |
| **性能** | maintex `auto/1024`，其余 `png/1024` + `colorArea` + `sharpen=edge` | → 130.5 MB（**−87.30%**，代价 22.0%） |

配套改动：
- `PresetIo.Envelope` 新增 `colorArea` / `sharpen` 两个字段，`TryReadPlanEx` 读回（老预设没这两个字段 → 默认开/空，行为不变）。
- 第 1 页 `ApplyProcessFlags()` 在套用预设时设置 `Resample.ColorMode` 与锐化参数，标签栏也会显示「彩色贴图也走面积平均+锐化」。
- `PreReload()`：第 0 项恒为「不使用预设」，三个基础预设固定顺序排在其后，其余用户预设跟在后面；**默认选中「仅优化」**。
- **踩坑：选中 ≠ 套用**。`PreReload` 在 `pre1Loading=true` 期间改 `SelectedIndex`，`SelectedIndexChanged → PreApply` 被守卫挡掉 → 启动时预设只是"显示选中"却没生效。修法：`finally` 之后再补一次 `PreApply`。
- **踩坑：勾选会覆盖计划**。`ApplyPlan` 原来先设尺寸再设勾选，而勾选触发的 `RowToggled` 会把"原尺寸(0)"改成默认 1024 → 只有主贴图（本来就勾着、不触发事件）保留了 0。修法：**先勾选，再用预设值覆盖**。

### 38.3 三个"无损其实有损"的修复（这才是本轮真正的技术发现）

用户问"原图本来就比重编码小的情况下，保分辨率 + 无损 PNG 重压缩 + 灰度双通道化能否无损降体积"。查证发现**之前做不到**：

1. **GDI+ 预乘 alpha 往返破坏像素**：`ToArgb → DrawImage(HighQualityBicubic) → EncodePng` 这一圈会把
   **alpha=0 像素的 RGB 清成 0**（实测 198→0）、半透明像素 RGB 出现 **±1~4** 抖动。
   某卡 22 张贴图里 7 张中招。修法：不需要改分辨率且结果是 PNG 时**完全绕开 GDI+**，
   直接对**原字节**做 `PngEnc.Recompress`（自己解 IDAT + 自适应滤波 + zlib-ng + 严格校验 R==G==B 的降通道）。
2. **无损优化路径被漏掉**：`if (use.Length >= raw.Length) { use = raw; }` 之后，
   `ReferenceEquals(use, pngNew)` 为假 → PNG 无损再压缩 / JPEG 无损哈夫曼**从没跑过**。
   修法：对 `ReferenceEquals(use, raw)` 的情况也试一次（仍"只在更小时替换"，`res.NKeepOpt/KeepOptSaved` 计数）。
3. **同尺寸插值**：1:1 的 `DrawImage` 即使 `HighQualityBicubic` 也会挪动像素。修法：新增
   `TexTool.CopyExact()`（逐像素精确拷贝），同尺寸时用它。

**验证**（`lossless_check.py`，14 张卡）：贴图载荷 1,063,593,410 → 832,743,305（**−21.70%**），
「逐字节相同 68 张 + 像素完全相同 143 张（66 张换通道类型）+ **有问题的 0 张**」。
副作用：JPEG 类贴图体积约 +1%（源像素不再被意外抹平），默认档整体 +0.16%（775.5 → 776.7 MB）。

### 38.4 教训

- **"无损"必须逐像素验证，而且要覆盖"没有改分辨率"这条路径**。之前的校验只覆盖了降采样路径，
  于是"保分辨率 + 重编码"这条潜在有损的路径一直没被测到。
- **"选中"和"生效"是两件事**：有守卫标志的 UI 回调里改状态，必须显式补一次应用。
- 预设里放不下的"处理开关"要么进预设，否则预设之间无法真正区分（这正是 colorArea/sharpen 字段的由来）。

## 39. 2.13：「独占贴图保护」——第一条按贴图（而非按类）的规则

### 39.1 用户要求

「分析 example 里的 Chronopattern Cartethyia B light.png，压缩后损失了大量精度，而且我注意到
**胸罩的独占贴图在本身不占用多少空间的情况下压缩后损失了大量精度**。同时我想知道 mask 和 normal
贴图如果删去色彩通道会对质量造成较大的影响吗？」
→ 随后：「加一个「独占贴图保护」。然后跟我说明智能档的判断逻辑。」

### 39.2 根因：类级上限 + 字节加权评估，双重低估「独占贴图」

在此之前整条流水线**只看一张贴图属于哪一类**（`TexTool.Classify`），从不看**它被几个部位引用**。

| | 8192² 掩罩，10 个部位共用 | 4096² 掩罩，只给「胸罩」用 |
|---|---|---|
| 类别表给的上限 | 1024 | 1024 |
| 实际降幅 | ÷8 | ÷4 |
| 误差落在哪 | 摊到 10 个部位，每个承担 1/10 | **100% 落在胸罩上** |

而报告里用的「按字节加权的精度代价」会因为独占贴图只占全卡 8.4% 字节而**低估**它。
两个偏差叠加 → 就是用户看到的「没什么体积、却损失大量精度」。

### 39.3 实现

`TexTool.ProtectOwn`（默认 true）/ `TexTool.ProtectOwnMax`（默认 4096），插在算缩放比之前：

```csharp
if (ProtectOwn && !keep) {
    List<int> ownSlots = null;
    if (texSlots != null) texSlots.TryGetValue(key, out ownSlots);
    if (ownSlots != null && ownSlots.Count == 1) {
        int pc = ProtectOwnMax > 0 ? ProtectOwnMax : Math.Max(w, h);
        if (pc > cap) { cap = pc; res.NOwnProtected++; log("[独占保护] …"); }
    }
}
```

`texSlots[TexID] = [部位 key…]` 由 `TexKeys()` 建，独占 ⇔ `Count == 1`。
**踩坑**：`texSlots` 原来只在 `slotRules != null || charaGroupMask != 0 || preset.Textures.Count > 0`
时才建 → 纯 `plan=` 模式下是 `null`，保护**一次都没命中**（开/关产物字节完全相同）。
修法：建表条件加上 `|| ProtectOwn`。

UI：第 1 页勾选框 + 上限下拉框（4096 / 2048 / 原尺寸不降）。
预设 JSON 新增 `ownProtect` / `ownMax`；命令行 `own=0|1`、`ownmax=N`，
显式命令行参数**优先于**预设文件里的值。

### 39.4 实测：上限越高，性价比反而越好

14 张 example 服装卡（1,077,229,777 字节），统一套「质量」类型表：

| 档位 | 产物 | 体积减小% | 精度代价% | 性价比 | 对比度保持 | 边缘保持 |
|---|---|---|---|---|---|---|
| 关 | 173,942,375 (165.9 MB) | 83.853 | 20.595 | 4.071 | 67.5% | 51.6% |
| 放宽到 2048 | 221,396,481 (211.1 MB) | 79.448 | 17.921 | 4.433 | 71.6% | 57.5% |
| 放宽到 4096 | 351,571,474 (335.3 MB) | 67.363 | 13.297 | **5.066** | 79.3% | 68.4% |

单卡（Chronopattern Cartethyia B light.png，80,671,160 字节）：

| 档位 | 产物 | 减小% | 代价% | 性价比 |
|---|---|---|---|---|
| 关 | 6,937,815 | 91.40 | 4.964 | 18.4 |
| 开 4096 | 15,364,798 | 80.95 | 4.335 | 18.7 |
| 全保（ownmax=0） | 15,712,582 | 80.52 | 4.334 | 18.6 |

被命中的 5 张里 **4 张变成逐像素完全相同**（完全无损），1 张 ÷8 → ÷2：

| TexID | 类 | 归属 | 尺寸 | 关 | 开 |
|---|---|---|---|---|---|
| 22 | mask | 胸罩 bra | 4096² | ÷4 SSIM 0.948 边缘 66.7% | 逐像素相同 |
| 23 | normal | 胸罩 bra | 4096² | ÷4 SSIM 0.938 边缘 42.7% | 逐像素相同（4.87→2.62 MB） |
| 16 | normal | 饰品槽 11 | 2048² | ÷2 SSIM 0.990 | 逐像素相同 |
| 17 | mask | 饰品槽 11 | 2048² | ÷2 SSIM 0.983 | 逐像素相同 |
| 2 | mask | 饰品槽 12 | 8192² | ÷8 SSIM 0.996 边缘 87.1% | ÷2 SSIM 0.999 |

**结论**：独占贴图「字节少、误差权重却大」，把字节花在它们身上，单位字节换回的画质
比花在别处更多 —— 所以上限越高性价比越高，取 4096 为拐点（再往上只多买回 0.4% 精度却再涨 5% 体积）。

**代价要如实说**：整卡体积翻倍。14 张里 7 张几乎不受影响（0 ~ +4.4%），另 7 张涨 90% ~ 430%。
涨得最狠的是「每个部位都自带一整套贴图」的模块化卡：
`CardA.png`（Cyber Cat 2，9 部件、4 张 maintex + 4 张 normal）
43.9 MB → 关：2.55 MB / 开：13.53 MB（**+430%**）—— 那种卡里几乎每张贴图都独占，
等于取消降分辨率。这类卡建议用 2048 或关掉。

### 39.5 通道删减（用户同批问题）的结论

- 工具**只在每个像素 R==G==B**（严格灰度）时降通道，且只在结果更小时替换 → **逐像素无损**。
  已逐张校验「✓同像素」（TexID 5 / 7 / 16 / 23）。单开这一项的收益：TexID 5 −12.1%、7 −20.7%、23 −34.6%。
- 非法线/掩罩经常不是严格灰度（TexID 6 只有 57.4% 灰度、22 有 90.8%、17 只有 1.8%、2 是 0%），
  这时工具**不会**降通道。
- **强行给彩色法线降灰的代价实测**：法线角误差均值 21.0°（p95 23.6°），
  Lambert 光照误差均值 14.1 级 / 最大 86 级 —— 直接毁光照。所以"只在严格灰度时降"是正确策略。

### 39.6 教训

- **「按类设上限」必然漏掉"这一类里最重要的那一张"**。字节加权是全局视角，
  天然低估"占比小但不可替代"的资产。**引用计数**是这里唯一便宜又可用的"重要性"信号。
- **别把「代码写好了」当成「功能生效了」**：保护逻辑第一次测试时开/关产物字节完全相同，
  根因是它依赖的数据结构 `texSlots` 在当前代码路径下根本没建。功能上线前必须做一次
  开/关对照，并**断言差异存在**。

## 40. 2.14：性能（183s → 25s，画质零损失）+ 删除「PNG 极限压缩」

### 40.1 用户要求

「首先删除「PNG 极限压缩」这个功能。然后直接按步骤执行软件优化并测试，测试没问题进行下一步。
然后避免你所说的不建议做的，并且更详细的跟我解释一下不建议做的每一条都是指什么。」

### 40.2 先测再改：瓶颈分布（新增 `KOITEX_TIME=1` 分阶段计时）

单卡 80.7 MB（15 张贴图），「仅优化」预设：

| 阶段 | 改动前 | 占比 |
|---|---|---|
| **反滤波 + 自适应滤波选表** | **9,297 ms** | **59%** |
| deflate（zlib 6） | 3,431 ms | 22% |
| inflate 原 PNG | 804 ms | 5% |
| 重采样 | 815 ms | 5% |
| 解码+分析 / PNG 编码 / 打包 | ~380 ms | 3% |

资源占用（200 MB 卡）：**平均 CPU 190% / 3200%**（32 核只用了 1.9 个），
**峰值工作集 2,745 MB**。

> 教训：**先量再改**。直觉会以为是 deflate（压缩）最贵，实际是"给每行挑滤波表"那个三重循环。
> 它每行跑 5 遍历、每遍逐字节回写候选行、还要 Clone；一张 8192² RGBA 就是 13.4 亿次内层迭代。

### 40.3 四处改动

**① 灰度优先打包**
原来"先算彩色版 → 再算灰度版 → 谁小用谁"。严格灰度贴图降灰后**信息完全相同、通道少 2~4 倍**，
灰度版几乎必然更小 → 彩色版整轮白算。实测送进 deflate 的 **412 MB 里 88 MB（21.4%）被丢掉**。
改成先算灰度版、只要比原图小就直接返回。`Pack` 调用 14 → 10 次，deflate 3,431 → 2,304 ms。

**② 自适应滤波：只算分不回写 + 行并行**
行与行完全独立（只用本行+上一行），且换哪张滤波表都不改变解码像素 → 可安全并行。
9,602 ms → 1,890 ms（**5.1×**）。开关 `KOITEX_NOPAR=1` / `KOITEX_PNG_THREADS=N`。

**③ 流式分块滤波 + 压缩**
去掉整幅 `rows((stride+1)*h)`（8192² RGBA = 268 MB）与 inflate 中间缓冲；
按 4 MB 一块：块内并行挑滤波表 → 块整块喂 deflate；解压侧改逐行流式。
**峰值内存 2,745 → 1,121 MB**。这是并行化的前提。

**④ 两级并行**
· 贴图级（单卡）：并行算每张贴图为 `TexJob`，再**串行按原顺序**写 msgpack、合并计数、按原顺序打日志。
· 卡级（文件夹批量）：CLI `jobs=N`、GUI 批量 `KOITEX_JOBS`，默认 4 路（受内存而非核数限制）。

**踩坑**：把主循环 361 行搬进本地函数时用脚本做文本变换，`res.` → `j.` 的盲替换把
`preset.Textures` 里的 "res." 也命中了（→ `Textuj`）。**机械替换必须加词边界**，
改完还要 grep 异常模式（`[A-Za-z_]j\.`）复查。

### 40.4 为什么"结果不变"是可证的

· 换滤波表不改像素；只算分不回写、行并行、块并行都只是重排**纯整数**计算 → 逐位等价。
· 唯一变化：deflate 按块喂入后，zlib-ng 对**喂入分块敏感**，同一张图的压缩流会有极微小差异
  （用 1 GB 单块可精确复现旧字节，证明原因就是分块）。全库合计 **-74 字节（-0.00002%）**。
· 验证：`lossless_check.py` 逐张贴图比对 2.13 与 2.14 产物 →
  「逐字节相同 203 张 + 解码后像素完全相同 8 张 + **有问题的 0 张**」；
  并行度扫描 jobs=1/4/8 × texjobs=1/4/8 产物**逐字节相同**。

### 40.5 实测

| 场景 | 2.13 | 2.14 | 提速 |
|---|---|---|---|
| Chronopattern Cartethyia B light | 15.5 s | 5.3 s | 2.9× |
| CardA.png（200 MB） | 36.2 s | 6.2 s | 5.9× |
| black Lunalice 11 ME-PRO | 17.8 s | 2.9 s | 6.0× |
| 兔旗袍（小卡） | 2.6 s | 1.5 s | 1.7× |
| **14 卡整批（1,027 MB）** | **183.4 s** | **24.7 s** | **7.4×** |

deflate 档位标定（同一张卡）：Fastest(1) 8.9s/+3.31% ｜ Optimal(6) 10.7s/基准 ｜
Smallest(9) 35.8s/**-2.89%** → 所以 level 9 直接删掉，不再做成选项。

### 40.6 教训

- **瓶颈要靠仪表，不要靠猜**：这次的 59% 在一个"看起来不像瓶颈"的滤波选表循环上。
- **"行间独立 + 整数运算" = 可以放心并行**：既拿性能又不承担画质风险。
- **内存是并行的隐形天花板**：不先把 2.7 GB/卡 降下来，4 路并发就是 11 GB，提速会变成换页。
- **批量改文件的文本变换要加词边界并复查**（见 40.3 的 `Textures` 事故）。

## 41. 2.15：锐化（可调强度 + alpha 一起锐化）+ 法线编码考证

### 41.1 用户要求

「按照上一轮的流程进行优化，如果可以的话加入一个自定义锐化程度的功能。」
上一轮定的次序是 A → F → B → E。本轮结果：**A 落地并设为默认；F 实现但实测不占优；B 考证后确认不做；E/换核不做。**

### 41.2 A：alpha 通道也锐化（唯一"改了纯赚"的改动）

问题：`UnsharpInPlace` 只锐化 BGR（`for c in 0..2`），**alpha 只降采样、从不锐化**。
而实测 **17 张掩罩里有 7 张（41%）的掩罩数据住在 alpha 通道**（典型：某卡 mask 8192² 的 alpha 方差占比 0.892）。

改法：`UnsharpInPlace(bmp, k, kEdge, alphaToo)`，只对 `IsDataClass` 贴图开 alphaToo
（彩色贴图的渐变 alpha 锐化会长硬边）。

**改动面验证**（14 张卡、只切这个开关）：逐位不变 174 张 / 变了 37 张；
**彩色类 0 张、RGB 变化 0 张、异常 0 条**；alpha 边缘锐度最高 148.7%；代价 +1,285,102 字节（+0.37%）。
**开关关掉后与 2.14 逐字节完全相同（14/14）** —— 证明本轮无意外改动。

### 41.3 自定义锐化强度

`Resample.SharpenPct`（0~200，默认 100）+ 界面滑条 + 预设字段 `sharpenPct`/`sharpenMode`
+ 命令行 `sharpenpct=` / `sharpcas=` / `alphasharp=`。

实机曲线（Chronopattern Cartethyia B light，「质量」表 + 独占保护）：

| 档位 | 产物字节 | 加权SSIM | 边缘保持 | alpha锐度 | 过冲% |
|---|---|---|---|---|---|
| 0% | 15,055,605 | 0.9671 | 61.8% | 78.2% | 0.09% |
| 50% | 15,230,342 | 0.9676 | 64.1% | 79.4% | 0.36% |
| **100%（默认）** | 15,410,174 | **0.9677** | 66.6% | 80.8% | 0.83% |
| 150% | 15,488,235 | 0.9675 | 69.0% | 82.1% | 1.36% |
| 200% | 15,534,637 | 0.9670 | 71.2% | 83.0% | 1.99% |

**加权 SSIM 在 100% 处最高** → 现默认就是这项指标的顶点，不用改。
注意过冲在 0% 时是 0.09%（Box 核本身不过冲），200% 时涨到 1.99%。

### 41.4 F：CAS 实测不占优

实现了 CAS（5 抽头十字 + 局部对比度自适应权重，`Resample.CasInPlace`）。实测：

| 方式 | 产物字节 | 加权SSIM | 边缘保持 | 过冲% |
|---|---|---|---|---|
| 边缘感知（默认，100%） | 15,410,174 | **0.9677** | 66.6% | **0.83%** |
| CAS（100%） | 15,501,480 | 0.9671 | 67.1% | 1.38% |
| 边缘感知（200%） | 15,534,637 | 0.9670 | **71.2%** | 1.99% |
| CAS（200%） | 15,549,749 | 0.9670 | 68.2% | 1.72% |

CAS 每项都不占优（过冲更大、SSIM 更低、体积更大，200% 时边缘保持差 3pp）。
→ 保留为可选项（界面下拉框 + `sharpcas=1`），**默认仍是边缘感知**，并在 tooltip 里写明实测结论，
不把它包装成升级。

### 41.5 B：法线归一化 —— 考证后**不做**

先考证编码，结果推翻了前提：

| 解码约定 | 61 张"法线"的平均向量长度 |
|---|---|
| RGB 直接当三维向量 | 0.813 |
| **XY-only（z = sqrt(1−x²−y²)）** | **1.045** |

→ 这批用的是 **XY-only 编码**，B 通道是辅助数据。所以"把 RGB 重新归一化"**会把 XY 编码扭曲掉**，是错的。

但**编码并不统一**：61 张里有 **13 张**在 XY-only 下也有 **100% 的像素 x²+y²>1**（重建 Z 无解）。
而且**源图自己就不满足**：中位数 7.28% 的像素 x²+y²>1（最大 100%）；锐化只让它再多 0.96pp（最大 19.9pp）。

→ 任何"自动归一化/自动夹回单位圆"都会改到本来不该动的像素，且必须逐卡猜约定。
**B 不做。** 这一条记为"查了之后决定不做"。

### 41.6 为什么不换降采样核

受控实验（256²、÷4、不夹紧）：阶跃边 Box 范围恰为 [0,255]、**过冲 0.00**；
Lanczos-3 是 [−4.34, 259.34]、过冲 8.67。1 像素亮线：Box 给 63.75（=255/4，精确面积平均，邻格 0）；
Lanczos-3 峰值仅 48.86，还把能量摊到邻格并压出 −16 的下冲。

真实贴图上换 Lanczos-3：边缘保持只 +1.3~2.4pp，体积 **+8%~+70%**（掩罩最惨：+70% 且 SSIM 反降）。
而"Box + 后面的锐化"拿到同样提升，**每百分点体积便宜 2~3 倍**。
→ 核不动。E（内容感知降采样，救细线）潜力最大（÷4 时 1 像素线只剩 25% 亮度，换核救不了），
但需游戏内动态验证，留作实验项。

### 41.7 教训

- **先考证前提，再动手**：B 看起来是"稳妥的改进"，考证后发现**前提就不成立**（编码不统一）。
  如果直接实现，会把一批本来正常的贴图改坏。
- **改一个开关要能证明"只有该变的变了"**：本轮用"关掉开关是否逐字节回到上一版" +
  "变更集里有没有彩色贴图/RGB 变化"两条硬约束卡住，比肉眼看图可靠得多。
- **实现 ≠ 有收益**：CAS 实现完了、也测了，结论是不占优 —— 那就如实标注，不要因为"写了就想用"而设为默认。
### 41.8 补记：第一版 A（alpha 全锐化）**过宽**，加闸门后才是对的

第一版实现对**所有数据类贴图**都锐化 alpha，测出来"影响 37 张、代价 +0.37%"，
看上去还挺合理。生成测试卡时顺手查了一下这 37 张的 alpha **到底是什么分布**，发现了反例：

| 卡 | TexID | 原 alpha（min/p5/中/p95/max/std） | 判定 |
|---|---|---|---|
| CardA.png | 4 | 61/127/**127/127**/197  std 1.0 | 占位常数 |
| CardA.png | 11 | 66/127/**127/127**/255  std 4.2 | 占位常数 |
| CardA.png | 15 | 1/127/**127/127**/252  std 13.9 | 占位常数 |
| Chronopattern Cartethyia B light | 1 | 0/0/0/**255**/255  std 118.2 | 0/255 双峰掩罩（真数据） |
| Chronopattern Cartethyia B light | 5 | 0/0/181/201/255  std 92.2（与亮度相关 1.00） | 冗余但真实 |

**p5 和 p95 都等于 127** 意味着绝大多数像素恰好是 127（≈0.5）——
那是作者工具写进去的**占位常数**，不是掩罩。锐化一条几乎恒定的通道，
"alpha 边缘锐度 +46.9%" 这个数字其实只是**离群点被放大**：画质无收益，体积白涨。

**闸门**：alpha 落在 [120,136] 的像素占比 > 95% → 判定为占位常数，跳过 alpha 锐化。

| | 影响贴图 | 代价 |
|---|---|---|
| 无闸门 | 37 张 | +1,285,102 字节（+0.37%） |
| **加闸门** | **26 张** | **+888,987 字节（+0.25%）** |

有意思的是：上一轮报"alpha 锐度提升最大"的那张卡（`CardA.png`，+46.9%）
在加闸门后**完全消失**——它的 alpha 正是占位常数，那个 +46.9% 纯粹是噪声放大。
生成测试卡时也因此能明确告诉用户："这张卡 2 与 4 完全相同，不用比"。

**教训**：
- **"我改动了 X" ≠ "X 有意义"**。指标（alpha 边缘锐度）能涨，也可能只是因为放大了一条本该平坦的通道。
  下结论前要看**被改动的数据本身长什么样**（分布、峰型、量级），而不是只看改动前后的比值。
- **同一个"数据类"标签下混着不同性质的东西**：掩罩的 alpha 是数据，法线的 alpha 常常是占位常数，
  还有的是"亮度的副本"。按类统一下手会误伤。
- **测试卡不只是给用户看的**：生成它的过程（要为每个档位写清楚"差别在哪"）逼着我去核对
  "差别是否真实存在"，这才发现了上面这个问题。
### 41.9 补记二：用户反馈「默认预设下完全看不出锐化的作用」—— 两个真问题

用户原话：「使用性能预设和质量预设重新制作测试卡，默认预设下完全看不出锐化的作用。」
首先复现确认，然后发现这不只是"选错预设"，还牵出一个 CLI 的静默退化。

**（一）锐化只在"真的降分辨率"时才跑，而默认预设从不降分辨率**

锐化在 `Resize()` 里，而 `Resize()` 里那段只在
`detail && DetailMode && (w < src.Width || h < src.Height)` 时执行 —— 也就是**必须真的在缩小**。
默认预设「仅优化」把 9 个类全设成 `size = 0`（原尺寸）→ 从不缩小 → 锐化一次都不会跑。

实测（同一张卡，只看 `sharpenpct=0` 与 `200` 的产物）：

| 预设 | 锐化0% | 锐化200% | 差值 | 生效？ |
|---|---|---|---|---|
| **仅优化（默认）** | 73,534,784 | 73,534,784 | **0** | **✗ 逐字节相同** |
| 质量 | 15,055,605 | 15,534,637 | 479,032 | ✓ |
| 性能（+colorArea） | 14,363,675 | 15,015,517 | 651,842 | ✓ |

「性能」的效果最大，是因为它带 `colorArea=true` —— 连彩色主贴图也走面积平均+锐化。

**修法**：滑条值变化时检查当前类型表，如果一个类都没设"最大边"，直接往日志里写提示
（`WarnIfSharpenInert`）。这是"UI 上摆着一个当前状态下永远不生效的控件"的典型坑。

**（二）命令行 `preset=` 只带了部位/单贴图规则，不带类型表 —— 静默退化**

`ParsePresetArg` 只构造 `TexTool.Preset`（部位规则 + 单贴图规则 + 通用设置），
**类级类型表在 CLI 里根本没被读**。于是 `preset=质量.json` 出来的东西和界面上选「质量」
完全不是一回事，而且**不报错、不提示**。我自己的测试卡生成脚本就踩了这个坑 ——
标着"质量预设"生成的卡，其实是别的东西（这也是为什么第一次做的测试卡说服力不足）。

连带一起丢的还有 `colorArea` / `sharpen`。修法：
1. `compress` / `batch` 在没给显式 `plan=` 时，改从 `preset=` 的 JSON 里读类型表；
2. `ApplyGlobalFlags` 里把预设的 `colorArea` / `sharpen` / `sharpenPct` / `sharpenMode` 一并应用
   （命令行显式写了对应开关时以命令行为准）。

验证：`preset=性能` 与 `plan= + colorarea=1 + sharpen=edge` 的产物**逐字节相同**。

**教训**：
- **"参数写进去了" ≠ "参数生效了"**。`preset=` 的语义在 GUI 和 CLI 之间不一致，
  两边都不报错 —— 这类"静默退化"只能靠**跨入口的一致性测试**发现（本例：同一预设、
  两种传法、比对产物字节）。
- **用户说"没效果"时，先复现再解释**。用户的观察是对的，而且比"你选错预设了"这句话背后
  能挖出的东西多得多：它同时暴露了一个 UI 提示缺失和一个 CLI 语义缺陷。
- **测试卡的生成脚本本身也要验证**：脚本给卡贴的标签（"质量预设"）必须能被证明，
  否则基于它得出的结论全是错的。
### 41.10 2.15 收尾：可手打百分比 + 「细节图保细节」的包含关系 + E 实测不占上风

**（一）锐化百分比可手打**
滑条右侧换成 `NumericUpDown`（0~200，带上下箭头），滑条 ↔ 数字框双向同步（`syncing` 防回环）。
拖滑条仍然方便，想精确指定 137% 就直接点进去打。交付版实测：`sharpenpct=137` → 15,466,156 字节，
正好落在 100%（15,410,174）与 200%（15,534,637）之间。

**（二）「细节图保细节」与「锐化」是包含关系 —— 这是第三个"死控件"陷阱**
`Resample.DetailMode`（「细节图保细节」）是**整条新降采样路径的总开关**：它打开时才做
① 面积平均代替 GDI+ 双三次、② 之后的低分辨率域锐化。**锐化跑在它里面**。

实测（质量预设、同一张卡）：

| 配置 | 锐化0% | 锐化200% | 差值 | 生效？ |
|---|---|---|---|---|
| 细节图保细节 ☑ | 15,055,605 | 15,534,637 | 479,032 | ✓ |
| **细节图保细节 ☐** | 15,170,354 | 15,170,354 | **0** | **✗ 逐字节相同** |
| 细节图保细节 ☑ + colorArea | 14,977,669 | 16,129,542 | 1,151,873 | ✓（最大） |

锐化要真跑起来必须**同时**满足四条：① 勾「细节图保细节」② 属数据类或开了 colorArea
③ 该贴图真的被降了分辨率 ④ 百分比 > 0。
`WarnIfSharpenInert()` 现在覆盖 ① 和 ③，动滑条或动勾选框都会在日志里提示。

> 这是本项目第三次遇到"界面上摆着一个当前配置下永远不生效的控件"
> （前两次：预设不套用、`preset=` 不带类型表）。**新增控件时要顺手问一句：
> 在什么配置下它是死的？那种情况有没有提示？**

**（三）实验项 E（结构保留降采样）—— 机制成立，但实测不占上风，默认关闭**

设计：不再单纯取块平均，而是先看块内有没有"少数派结构"（细线），有就把输出朝该方向的极值推；
块内均衡处（真正的边）完全不动。`push = gamma·max(0, 1−2r)`。

受控图（256²、÷4）**它确实做到了**：

| 测试图 | Box | E γ=0.3 | γ=0.6 | γ=1.0 |
|---|---|---|---|---|
| 阶跃边/棋盘/渐变/纯平 | 一致 | **完全一致** | **完全一致** | **完全一致** |
| 1 像素亮线 峰值 | 63.75 | 92.44 | 121.12 | **159.38**（邻格仍为 0） |

但真实贴图上不占上风（质量预设）：

| 卡 / 方案 | 产物字节 | 加权SSIM | 边缘保持 | 过冲% |
|---|---|---|---|---|
| Chronopattern 面积平均+锐化100% | 15,410,174 | **0.9677** | 66.6% | 0.83% |
| Chronopattern 只加锐化200% | 15,534,637 | 0.9670 | 71.2% | 1.99% |
| Chronopattern E0.6+锐化100% | 15,567,377 | 0.9634 | 72.3% | 1.70% |
| 蕾丝裙 面积平均+锐化100% | 27,872,615 | 0.7565 | 64.6% | 2.10% |
| 蕾丝裙 只加锐化200% | 28,144,113 | **0.7693** | **72.7%** | 7.25% |
| 蕾丝裙 E0.6+锐化100% | 27,911,892 | 0.7523 | 65.6% | 2.69% |

**按"同等过冲"比较，E 与直接把锐化调高基本打平**（蕾丝那张：约 2.7% 过冲处，
E0.6 给 65.6% 边缘、锐化约 100~120% 给 65.5%），而 E 还更贵（体积更大、SSIM 更低）。
蕾丝那张更直接：只加锐化 200% 的边缘保持（72.7%）比 E1.0（67.1%）还高。
→ 默认关闭，只留 `struct=` 开关 + `测试卡_锐化\E实验\` 的对比卡。

> **注意一个方法论前提**：面积平均本身就是该块在低分辨率网格上的**精确投影**，
> 所以任何偏离都必然降低 SSIM。SSIM 在这里只能回答"谁更远离原图"，
> **回答不了"细线是否还在"** —— E 的理论优势恰好落在它测不到的地方。
> 所以 E 的最终判决仍然交给游戏内对比卡（这也是为什么即使指标不利也要把它做出来测完）。

**（四）本轮踩到的操作坑**
- `uitest ... prelist=1`（不带 `snap=`）跑 SC 版会**残留隐藏窗口进程**，把 EXE 锁住，
  后续 `Copy-Item` / `Compress-Archive` 全失败，而且**旧 exe 会静默继续被使用** ——
  我第一次的 `struct=` 验证因此跑的是旧 exe（结果等于基线，看起来"开关没生效"）。
  教训：**打包后用新开关做一次"值确实变了"的验证**，否则你证明不了跑的是新版本。
- 生成文档的脚本里用 `-replace` 往三引号字符串里插代码块，容易插错位置导致
  小节重复/丢失。改成**锚点唯一且不含引号**、并逐次核对成文标题列表。
## 42. 2.16：锐化独立成一步（对所有贴图 / 只对降过分辨率的贴图）+ 顺序考证

### 42.1 用户要求

「将锐化独立出来，修改锐化运行的条件。锐化对所有贴图都生效。只有降低了分辨率的贴图才需要跑锐化。
细节图保细节在锐化后执行是可行的吗？」

### 42.2 改了三处

| 项 | 改前 | 改后 |
|---|---|---|
| 是否受「细节图保细节」约束 | 锐化写在 `DetailMode` 分支**里面**，去掉勾就完全不跑 | 锐化是独立第 3 步；那个勾**只决定降采样核** |
| 生效范围 | 只有数据类（法线/掩罩/高度），彩色要另开 colorArea | **所有类型**，含主贴图 |
| 运行条件 | 类 + DetailMode + colorArea + 降分辨率 + pct>0 | **只有一条：这张贴图真的降了分辨率**（+ pct>0） |

新流程：`降采样（细节图保细节在这里选核）→ 降噪 → 锐化 → 可选灰度打包`

实测（Chronopattern Cartethyia B light）：

| 配置 | 改前 | 改后 |
|---|---|---|
| 细节图保细节 ☑，锐化 0%/100% | 15,055,605 / 15,410,174 | 15,055,605 / **15,982,955** |
| 细节图保细节 ☐，锐化 0%/100% | 15,170,354 / **15,170,354（差 0）** | 15,170,354 / **16,035,046（差 866,670）** |
| 仅优化（不降分辨率），锐化 0%/100% | 逐字节相同 | **逐字节相同**（符合设计） |
| 性能（已开 colorArea），锐化 100% | 14,905,932 | 14,914,475（+8,543） |

代价：质量预设锐化 100% 从 15,410,174 → 15,982,955（+3.7%）。

### 42.3 顺带修掉的真问题：彩色贴图透明边缘的彩边

既然彩色贴图也锐化了，就必须处理 alpha：彩色贴图的 alpha 是**覆盖率**，
全透明区的 RGB 常是垃圾值，直接对 RGB 做 unsharp 会把垃圾一起放大 → 半透明边缘出彩边。

改法：**在预乘 alpha 空间里锐化**（先 BGR×A，锐化后再解回直通色），
外加"覆盖率 < 32 的像素不动"（解回直通色要除以 a，会放大量化误差 255/a 倍）。

实测（半透明区彩色偏差，越小越好）：不预乘 **9.46** → 预乘 **8.68**；
基线（完全不锐化）4.41 —— 剩下的增量是**边缘对比度被正常提高**带来的，不是彩边。

### 42.4 「细节图保细节」能不能放到锐化之后？—— 不能，且已实测

「细节图保细节」现在的含义就是**降采样核的选择**（面积平均 vs GDI+ 双三次），
而降采样必须发生在锐化**之前**。反过来做的话，锐化加的高频会被随后的面积平均当噪声抹掉。

实测（同一张卡、同一 USM 核）：

| 贴图 | A 降采样→锐化（现在） | B 锐化→降采样 | C 强锐化(2.0)→降采样 | 参考·只降采样 |
|---|---|---|---|---|
| maintex 4096² | **99.4%** | 65.1% | 66.5% | 60.6% |
| normal 8192² | **84.5%** | 51.5% | 52.3% | 48.0% |
| mask 8192² | **84.1%** | 52.1% | 53.2% | 46.7% |

**提前锐化的强度加 3.3 倍（0.6→2.0）只多换 1.4 个百分点，而放到降采样之后能拿 38.8 个百分点**
（maintex）。即"提前锐化 ≈ 全废"。所以唯一正确的顺序是
`降采样 → 降噪 → 锐化`，这也意味着"决定降采样核的那个开关"不可能排在锐化之后。

### 42.5 教训

- **一个开关同时管两件事，早晚会拧巴**：「细节图保细节」原来既选降采样核、又当锐化总闸，
  于是"想锐化但不想换核"或"想换核但不想锐化"都做不到。拆开之后两个开关各自语义清晰：
  `kernel=area|bicubic` 管核，`sharpenpct` 管锐化。
- **改"生效范围"必须先想清楚新增对象的特殊性**：把锐化扩到彩色贴图，立刻暴露了
  "预乘 alpha"这个在数据贴图上不存在的问题（数据贴图的 alpha 是独立掩罩，不做预乘）。
  扩范围时要把新对象当**新场景**重新审一遍，而不是假设老逻辑直接适用。
- **顺序类的问题不要靠推理，直接测**：提前锐化"看起来像"更保留细节（在原分辨率上操作嘛），
  实测才知道它 90% 被平均掉了。
## 43. 2.17：界面整理 + CAS 设为默认 + 删掉外部编码器

### 43.1 用户清单（8 项）与落地

| # | 要求 | 落地 |
|---|---|---|
| 1 | 对比度自适应设为默认锐化算法 | `Resample.SharpMode = "cas"`；内置预设一并写 `sharpenMode: cas` |
| 2 | 数值框可超过 200% | 数字框上限 0~**500**（滑条仍 0~200，超过时顶到最右）；CAS 的 peak 放开到 −1.0 饱和 |
| 3 | 移除「JPEG 用外部编码器」 | 删 `ExtJpeg.cs`、界面勾选框、`extjpeg=` 开关、日志、`mozjpeg\` 目录与文档 |
| 4 | 锐化单独一行 | 选项区改成 3 行 |
| 5 | 细节图保细节放锐化右边 | 同第 2 行 |
| 6 | 独占贴图保护单独一行放锐化下面 | 第 3 行 |
| 7 | 人物卡组过滤默认收起、人物卡自动展开 | 折叠行 + `AutoExpandGroupFilter()` |
| 8 | 扫描按钮还有必要吗 / 加大开始压缩 | 扫描改成自动、按钮删除；开始压缩加粗放大 |

### 43.2 「扫描」按钮的结论：可以删 —— 因为它只是"没自动化"的补丁

它做的事：读每张输入卡 → 按类统计「张数 / 字节 / 最大边」填进右侧「扫描结果」列 → 顺带把每张卡的
贴图清单打进日志。**从不修改任何东西**。

它在今天之前**唯一不可替代的理由**是：**拖入卡片会自动扫描，但「浏览…」和手输路径不会**。
把这两个入口补上自动扫描之后，按钮就多余了：

- 「浏览…」选中文件/文件夹 → 立即扫描；
- 手输路径 → **Enter** 或**离开焦点**时扫描（只在路径有效、且与上次不同时才扫，避免打字时反复触发）；
- 拖入 → 本来就会扫。

删除的风险与兜底：**「开始压缩」完全不依赖扫描结果**（`DoRun` 会自己重新读卡），
所以即使自动扫描没跑或失败，也不会卡住任何流程；而且自动扫描前先检查路径是否存在，
不会像直接调 `Inputs()` 那样弹出"路径不存在"的对话框打断打字。

保留：扫描本身、右侧「扫描结果」列、每张卡的日志。只去掉那个按钮。
顺带把顶栏提示从「先点①扫描」改成「选好输入卡，然后点「▶ 开始压缩」」。

### 43.3 踩到的布局坑（值得记）

把选项区从"自动换行的一行"改成"固定三行"时，先用 `TableLayoutPanel` 嵌 `TableLayoutPanel`，
结果**子面板的 PreferredSize 被算大**：第三行只需要 31px 却占了 **192px**，
把上面「类型表」那一行挤到只剩 3 行可见（9 行里 6 行看不到）。`Dock=Fill`/`Dock=Top` +
`AutoSize` 的各种组合都试过，PreferredSize 依然偏大。

最终改成 **`FlowLayoutPanel` + `FlowDirection.TopDown` + `WrapContents=false`** —— 它只做垂直堆叠，
不参与"抢高度"的协商，三行立刻变成 51/51/31。窗口默认高度相应加到 1360×942，
保证「类型表」9 行在**组过滤收起/展开两种状态下**都完整显示（实测 330px / 297px，都需要 ≥286px）。

**排查手段**：`uitest ... rects=1` 会 dump 每个控件的实际矩形和 PreferredSize，
一眼就能看出"谁比它需要的更高/更矮"。这比反复截图猜快得多。

### 43.4 CAS 设为默认的一个副作用（要提醒用户）

内置预设重建后是 `cas`，但**用户自己存过的老预设里写的是 `edge`**，套用时会按预设走。
这不是 bug（预设的本分就是记住你选的东西），但如果用户觉得"锐化好像变了"，这就是原因。
已在 `使用说明.txt` 里写明，并提示"重新存一次预设即可"。

### 43.5 教训

- **一个按钮"还需要吗"，先问"它是不是在补某个没自动化的入口"**。补上自动化之后，
  删除是净收益；但要同时确认**没有任何流程依赖它**（本例：压缩不依赖扫描）。
- **UI 布局的"自动协商"不可信**。嵌套容器 + AutoSize 的 PreferredSize 会互相打架，
  遇到"某块被挤扁/莫名空白"要立刻用 `rects` dump 看真实尺寸，不要靠猜。
## 44. 2.18：老预设自动跟随新默认算法 + CAS 强度曲线重标

### 44.1 「迁移」的正确做法：给"用户是否明确选过"留一个标记

2.17 把默认锐化算法从 `edge` 换成 `cas`，但**预设里存的 `sharpenMode` 照单全收**，
于是 2.15/2.16 存下的预设（那时默认是 edge）会被永远钉在 edge 上。
难点在于：预设里的 `edge` **无法区分**"当时默认值"和"用户真挑过"。

做法：预设新增 `sharpenModeExplicit` 标记 ——
- `true`（用户在界面上动过下拉框 / 命令行写过 `sharpcas=`）→ 按预设走；
- 无标记 / `false`（2.17 及以前）→ **不覆盖**当前值 = 跟随当前默认。

这样**老预设自动迁移到 cas**，而明确选过 edge 的预设不会被悄悄改掉。

实测（含等价性核对）：

| 预设 | 产物字节 | 结果 |
|---|---|---|
| edge、无标记（老预设） | 16,203,932 | CAS（迁移）✓ |
| cas、无标记 | 16,203,932 | CAS ✓ |
| **edge + Explicit=true** | 16,024,206 | edge（尊重选择）✓ |
| cas + Explicit=true | 16,203,932 | CAS ✓ |

显式 edge 预设 == 同预设加 `sharpcas=0`（都是 16,024,206）✓

> 一般化的教训：**"默认值"和"用户的明确选择"混在同一个字段里，是没法迁移的。**
> 要么留一个"是否明确指定"的标记，要么在写入时就把默认值省略掉（只在非默认时才写）。

### 44.2 顺手修掉「边缘感知」名不副实

`SharpenEdgeMax` 默认是 0，而 `SharpenEdgeMax == 0` 时 `UnsharpInPlace` 走的是**均匀锐化**分支
（`edgeMode = kEdge > k` 为假）。也就是说下拉框里写着「边缘感知」，实际做的是均匀 USM。
现在选中「边缘感知」会把 `SharpenEdgeMax` 抬到 `max(2.5, Sharpen)`。默认是 CAS，所以影响面很小。

### 44.3 CAS 强度曲线（本轮主要交付）

之前那张 0/100/200% 的表是 **USM** 的，默认换成 CAS 后必须重标。
工具实测（Chronopattern Cartethyia B light，「质量」表 + 独占保护）：

| 档位 | 产物字节 | 加权SSIM | 边缘保持 | alpha锐度 | 过冲% |
|---|---|---|---|---|---|
| CAS 0% | 15,055,605 | 0.9671 | 61.8% | 78.2% | 0.09% |
| **CAS 25%** | 16,130,246 | 0.9671 | 68.0% | 80.9% | 1.21% |
| CAS 50% | 16,155,544 | 0.9671 | 68.3% | 81.1% | 1.27% |
| **CAS 100%（默认）** | 16,203,932 | 0.9670 | 68.8% | 81.3% | 1.40% |
| CAS 150% | 16,276,730 | 0.9669 | 69.6% | 81.7% | 1.57% |
| CAS 200% | 16,366,257 | 0.9668 | 70.4% | 82.1% | 1.77% |
| CAS 300% | 16,637,266 | 0.9662 | 73.0% | 83.3% | 2.31% |
| CAS 500% | 17,687,735 | **0.9576** | 92.9% | 93.6% | **5.98%** |
| USM 100% | 16,024,206 | 0.9665 | 69.1% | 82.1% | 1.21% |
| USM 200% | 16,235,835 | 0.9643 | 75.4% | 85.3% | 2.47% |

**四条结论**：
1. **CAS 曲线在 25% 之后非常平**：0→25% 边缘 61.8%→68.0%（+6.2pp / +7.1% 体积），
   25%→100% 只再多 0.8pp（还要 +0.5% 体积）。第一档就吃掉了大部分收益。
2. **CAS vs USM 各有胜负**：100% 时 USM 更小、边缘更高、过冲更低；CAS 的 SSIM 略高。
   200% 时 USM 把边缘推得更狠（75.4% vs 70.4%）但 SSIM/过冲都更差。→ USM 更猛、CAS 更温和。
3. **>200% 对 CAS 基本没用，500% 直接崩**（SSIM 0.9576、过冲 5.98%）。
   数字框能填到 500%，但 CAS 的实际可用区间只到 ~250%。
4. **性价比拐点在 25~50%**：边缘保持≈边缘感知 100%，体积还小一点。

### 44.4 教训

- **换默认值之前先想"老配置怎么办"**。默认值一变，所有把它写进文件的旧配置都会把旧默认当成用户意愿。
  要么加显式标记，要么写入时省略默认值。
- **给数值框放开上限，要同时验证"放开之后还能用"**：CAS 500% 是能跑，但输出已经不可用
  （SSIM 掉 1 个百分点）。放开限制和标定可用区间是两件事，都要做。
---

## 45. matcap 转 JPEG 考证：能做，但收益 100% 来自丢 alpha；加"圆掩罩闸门"后保住 0.42%

**起因**：上一轮 `NonalphaJpeg`（按 shader 名判断能不能转 JPEG）被证伪，但用户游戏内实测反馈
「matcap 实际上转成 jpg 后在游戏内显示基本无影响」。于是换一个判据重做：**不看着色器，看贴图类型**，
把 matcap 类强制转 JPEG，量一下到底能省多少、以及省的是什么。

测试开关：`forcejpg=<类名列表>`（例如 `forcejpg=matcap`、`forcejpg=matcap,reflection`），
外加 `forcejpgsafe=0|1|2` 三道闸门。**仅测试用，不进主线、不进 GUI、不发给用户。**

### 45.1 实测结果（14 张服装卡语料，质量档 = 主贴图 auto/2048 + 其余 auto/1024）

| 方案 | 产物 | 相对基线 | 说明 |
|---|---|---|---|
| 基线（当前 2.18 默认） | 357,634,582 | — | |
| `forcejpg=matcap` 全转 | 355,864,540 | **省 1,770,042（0.49%）** | 不管 alpha，直接全转 |
| `forcejpg=matcap` + 闸门①（只转 alpha 平坦） | 357,634,582 | **省 0（0.00%）** | 与基线**逐字节相同** |
| `forcejpg=matcap` + 闸门②（圆掩罩安全规则） | 356,122,243 | **省 1,512,339（0.42%）** | 只拦下 2 张 |
| `forcejpg=matcap,reflection` 全转 | 355,742,540 | 省 1,892,042（0.53%） | reflection 只多贡献 0.03% |

逐卡（只有 6 张卡有变化，8 张 = 0.00%）：

| 卡 | 基线 | 全转 | 闸门②圆掩罩 | 闸门②省% |
|---|---|---|---|---|
| 127322340_薄纱蕾丝裙黑白 | 27,992,898 | 27,715,822 | 27,715,822 | 0.99% |
| CardA.png | 49,016,908 | 48,984,992 | 48,984,992 | 0.07% |
| CardA.png | 26,183,016 | 25,560,572 | 25,818,275 | 1.39% |
| CardA.png | 8,933,505 | 8,670,786 | 8,670,786 | 2.94% |
| CardA.png | 15,157,260 | 14,863,740 | 14,863,740 | 1.94% |
| black Lunalice 11 ME-PRO | 53,220,444 | 52,938,077 | 52,938,077 | 0.53% |
| 其余 8 张 | — | 无变化 | 无变化 | 0.00% |

### 45.2 「闸门①省 0」是这一轮最有价值的一条

闸门①只允许转 alpha **恒为 255** 的贴图，结果收益**归零、产物逐字节相同**。这说明：

- 现有 `auto` 规则本来就是 `useJpg = flatAlpha && !Risky(pl)` ——
  语料里 29 张 matcap，**alpha 平坦的 19 张在基线里早就是 JPEG 了**，强制开关对它们毫无作用。
- 基线里还是 PNG 的那 **10 张，全部**是 alpha 非平坦的；工具保留它们正是因为它们带 alpha。
- 所以"强制 matcap 转 JPEG"**精准地只挑中了有东西可丢的那 10 张**。
  0.49% 的收益，一分钱都不是白来的 —— 和上一轮 `NonalphaJpeg` 是**同一个陷阱**。

逐张贴图的账能对上：10 张非平坦 matcap 省下的字节数合计 = 1,770,042，与整卡批跑的结果**完全一致**。

### 45.3 那 10 张的 alpha 解剖（决定闸门②怎么定）

| 类型 | 张数 | 可省字节 | alpha 特征 | 转 JPEG 安全吗 |
|---|---|---|---|---|
| A 无透明区（只有量化噪声） | 5 | 690,177 | 均值 254.8~255.0、σ0.9~1.9，98.4%~99.8% 的像素**就是 255** | **安全**：源 PNG 从 JPEG 往返过一次，254/255 抖动而已，没有透明区域可丢 |
| B 圆边掩罩 | 4 | 872,428 | alpha=0 占比 20.9%~21.3%，而 **1-π/4 = 21.46%** | **安全**：透明区就是"正方形减内切圆"的四个角，标准 matcap UV 取不到 |
| C 真形状掩罩 | 2 | 257,703 | 内切圆**内**仍有透明（14.04% / 0.97%） | **不安全**：丢了会让掩罩区变不透明 |

B 类的逐像素验证（内切圆判据：像素中心归一化到 [-1,1] 后 r>1 为圆外）：

| 卡 / TexID | 圆外为 0 的比例 | 圆内为 0 的比例 | 圆外却 =255 的比例 |
|---|---|---|---|
| 薄纱蕾丝裙黑白 / 4 | 99.4% | 0.01% | 0.60% |
| black Lunalice / 4 | 99.4% | 0.01% | 0.60% |
| 180 / 6 | 99.4% | 0.01% | 0.60% |
| 147 / 73 | 96.3% | 0.13% | 0.00% |
| 147 / 71 | 53.2% | **12.10%** | 37.55% |

前四行的"0 区域"就是内切圆外，且**圆内几乎一个透明像素都没有** → 丢 alpha 在几何上不可见。
这解释了用户"游戏内基本无影响"的实测：**不是运气好，是 UV 根本采不到那些像素**。
147/71 则是真掩罩，必须拦。

### 45.4 闸门②的规则与实现

```
alphaCircleOnly = （a < 200 的像素里，落在内切圆内的比例 ≤ 0.5%）
允许转 JPEG  ⟺  flatAlpha  ||  alphaCircleOnly
```

在 `Analyze()` 里多吐一个 `alphaCircleOnly`，顺手把内切圆判据算出来（一次像素遍历，零额外开销）：
像素中心 `dx=(x+.5)/w*2-1, dy=(y+.5)/h*2-1`，`dx²+dy² ≤ 1` 即在圆内。

实测放行 8 张（1,512,339 字节）、拦下 2 张（257,703 字节），账目精确对上。

⚠ 特别注意 `Analyze()` 里的提前退出（`!flatAlpha && !gray && nmid*20 > n` 时 return）——
一旦提前返回，`alphaCircleOnly` 保持默认 `false`（保守，判为不安全）。这是有意的。

### 45.5 顺带澄清：上一轮的"反光"不是 matcap 干的

上一轮 `black Lunalice 11 ME-PRO__开.png`（−41.8% 那版）同时丢了 **6 张**贴图的 alpha：

| TexID | 类 | alpha 均值 | =255 占比 | 闸门②现在会转吗 |
|---|---|---|---|---|
| 1 | normal | 126.9 | 0.0% | 否 |
| 4 | matcap | 200.6 | 78.7% | **是**（圆边掩罩，圆内只 0.01% 透明） |
| 5 | maintex | 201.5 | 68.3% | 否 |
| 7 | normal | 127.8 | 0.0% | 否 |
| 11 | maintex | 199.6 | 71.6% | 否 |
| 12 | normal | 127.5 | 0.0% | 否 |

matcap 那张的透明区在圆外（采不到），所以**"凭空多出反光"更可能来自 maintex**：
KKUTS 把主贴图 alpha 当**反光/光泽强度掩罩**（均值 0.79、σ0.36 的连续掩罩，不是剪影），
丢掉 → 掩罩恒为 1.0 → 反光满值。这与用户"matcap 转 jpg 无影响"**不矛盾**：两件事的元凶不是同一张。

### 45.6 结论与是否上主线

- **0.42% 是真的、可安全拿到的**（14 张卡 1.51 MB；对 1.03 GB 原始体积是 0.14%）。
- 但**收益太小**：0.42% 换一个"按类型强制转 JPEG"的新开关 + 一套新判据，
  和当初被砍掉的渐进式 JPEG、PNG level 9 属于同一量级。**建议不进 GUI、不进默认路径。**
- 如果要上，正确形态是**可选项**（默认关）：`matcap 转 JPEG（圆掩罩安全规则）`，
  并且实现里必须带闸门② —— 带闸门①等于什么都没做（省 0）。
- **`forcejpg=` / `forcejpgsafe=` 保持测试开关**（CLI only，默认 `ForceJpgClasses` 空、`ForceJpgAlphaLevel=0`）。

### 45.7 教训

- **"能转"和"转得值"是两件事**：0.49% 的收益要先花力气证明它安全，证明完只剩 0.42%。
- **闸门①（省 0）是免费的探测器**：任何"按名/按类强制转格式"的开关，
  先加上"只转 alpha 平坦的"跑一遍 —— 省 0 就说明收益 100% 来自丢数据。
- **判据要落在数据形状上，而且要看形状**：`flatAlpha`（全 255）太严，
  会把"JPEG 往返噪声型 254"和"圆边掩罩"一并误判成"不能用"。
- **21.46% 这个数字是钥匙**：看到 alpha=0 占比 ≈21.3% 就该想到"正方形减内切圆"，
  进而想到 matcap 的 UV 只采样内切圆、四角取不到。
- 仍然**不要**用 shader 名当判据（§44 前一轮的教训）。
---

## 46. 「保护alpha」是空转控件；把 matcap 的 alpha 判据推广到全局 → 1.53%（大头在主贴图）

**起因**：用户问「能不能用 matcap 那个圆掩罩阀门代替现在版本的『保护alpha』选项」。

### 46.1 先查「保护alpha」到底在干什么 —— 结果它在默认流程里一次都没被读到

`TexTool.cs` 里 `protectAlpha` **只有一个使用点**（格式判定那一行）：

```csharp
useJpg = pref == "jpeg" ? !(protectAlpha && !flatAlpha)      // ← 只有这一条读它
       : (pref == "png"  ? false
       : (flatAlpha && !Risky(pl)));                          // auto：不看 protectAlpha
```

- 格式 = `auto` 时**根本不读**它 —— auto 自带 `flatAlpha` 检查，本身就是安全的。
- 全局模式 `mode=auto` 同样不读。
- 三个内置预设（仅优化 / 质量 / 性能）里**一格 `jpeg` 都没有** →
  **默认流程下勾不勾它完全没区别**。这是本项目第 4 个"空转控件"
  （前三个：预设选择没生效 / CLI `preset=` 丢类型表 / 「细节图保细节」门槛）。
- 它唯一能起作用的情形（用户在类型表里点「全部 JPEG」+ 把这个保护关掉），
  恰好就是上一轮 `NonalphaJpeg` 出事故的路径。**它该做的是更严，而不是留给用户关掉。**

### 46.2 把判据从「alpha 是否全 255」换成「alpha 里有没有数据」

新增闸门等级 `forcejpgsafe=3|4`（`ForceJpgAlphaLevel`）：

- **lvl3**：`alphaNoData`（**一个 a<200 的像素都没有**，只有 JPEG 往返的 254/255 抖动）
  → 视同平坦，**对所有类生效**（不再只作用于 `forcejpg=` 指定的类）。
- **lvl4**：在 lvl3 之上，matcap 类再放行"透明像素全在内切圆外"的（圆边掩罩，见 §45）。

实测（14 张服装卡，质量档，基线 357,634,582）：

| 方案 | 产物 | 相对基线 |
|---|---|---|
| 基线 | 357,634,582 | — |
| 只做 matcap（圆掩罩闸门，§45） | 356,122,243 | 省 1,512,339（0.42%） |
| **lvl3** | 352,179,100 | **省 5,455,482（1.53%）** |
| **lvl4** | 351,356,938 | **省 6,277,644（1.76%）** |
| 回归 lvl0 | 357,634,582 | 14/14 **逐字节相同** |

**换个适用范围，收益差 4.2 倍。**

### 46.3 收益结构：大头不是 matcap，是「alpha 是占位常数」的主贴图

lvl3 只换了 7 张贴图：

| 类 | TexID | 尺寸 | alpha 形状 | 省字节 |
|---|---|---|---|---|
| maintex | 4 | 2048² | **恒等于 230（100%）** | 2,772,918 |
| maintex | 28 | 2048² | 99.805% = 255，0.195% = 242 | 1,992,387 |
| matcap | 18 | 3083×3071 | 99.78% = 255，其余 222~254 | 206,156 |
| matcap | 46 | 512² | 99.2% = 255，其余 230~254 | 364,741 |
| matcap | 21 | 256² | 98.4% = 255 | 31,916 |
| matcap | 2 / 17 | 256² | 98.5% = 255 | 55,120 / 32,244 |

→ **matcap 合计只占 690,177（13%）；两张 maintex 占 4,765,305（87%）。**

### 46.4 风险与天花板

- matcap 那 5 张：alpha 最小 222~230，99% 的像素就是 255，其余是量化抖动 → **低风险**。
- `180/TexID4`：alpha **恒等于 230**（一个像素都不例外，230/255 = 0.902）。
  若 KKUTS 用主贴图 alpha 当反光/光泽强度掩罩（上一轮"凭空多出反光"正是这个机制），
  丢掉它会把整张材质反光**统一抬 11%** —— "均匀变亮一点"最容易漏看，**必须游戏内 A/B**。
- `薄纱/TexID28`：99.8% 就是 255，只有 0.2% 是 242 → 基本平坦，**低风险**。
- **天花板很小**：当前输出里还剩 327.8 MB PNG，其中
  **C 有真透明区 110 张 315.4 MB（96.21%）一律动不了**；
  A 常数 alpha 6 张 3.37 MB（+ Risky 类 10 张 6.02 MB）、B 无 a<200 6 张 3.05 MB。
  → 整个"alpha 是占位值"的思路**最多摸到 ~6.4 MB**。

### 46.5 建议形态（回答"能不能代替"）

- **可以删掉「保护alpha」复选框**，但理由不是阀门替代了它，而是**它本来就是空转的**，
  而且它的保护方向是对的（不该由用户关掉）。
- 正确做法：把"保护 alpha"从**可选复选框**升级为**内建判据**，再叠加"能证明没数据就放行"：

  ```
  允许 JPEG ⟺ alpha 里没有"真数据"
  真数据 = 存在 a < 200 的像素（说明有掩罩）
          例外：matcap 且这些像素全在采样圆外（圆边掩罩）→ 也当没有数据
  ```

  这样 UI 少一格、默认流程自动拿到 1.5~1.8%，而且**比现在更安全**
  （现在用户在类型表点一下「全部 JPEG」，保护就全没了）。
- 代价：失去"我就是要 jpeg、接受丢 alpha"的能力 —— 三个内置预设都不用这条路，
  不值得为它留一个会误导人的复选框。

### 46.6 教训

- **问"能不能代替 X"之前，先查 X 到底在干什么**。这次查出来的答案是"它一次都没被读到"。
- **同一个判据换个适用范围，收益能差 4 倍**（matcap 0.42% → 全局 1.53%）。
  写完一个"针对某类的特例"之后，一定要问一句"这个判据本身能不能全局用"。
- **"alpha 是常数"比"alpha 平坦"更有价值**：平坦（全 255）的 auto 早就转 JPEG 了，
  真正被漏掉的是**常数但不是 255** 的那些（230 / 127 这类占位值）。
- 但**占位常数可能真是设置**：230 完全可能就是作者设的"反光 90%"。
  判据能证明"里面没有空间信息"，**证明不了"这个数不重要"** —— 后者只能靠游戏内看。
- 加新闸门等级时**记得放宽 CLI 的 clamp**：`forcejpgsafe=3/4` 第一次跑出来收益 0，
  原因是解析处写了 `lv > 2 ? 2 : lv`，3 和 4 被静默夹成 2（而 2 只作用于 `forcejpg=` 指定的类）。
---

## 47. 2.19：α≡230 的主贴图被游戏内实测否决；alpha 保护改内建判据；「全部 JPEG」恢复名副其实

**本节先撤回 §46.3 / §46.5 的建议。**

### 47.1 游戏内实测：lvl3 那条路是错的（用户验证）

§46 里按"alpha 里没有 a<200 的像素 = 没有数据"放行了 `CardA.png` 的主贴图
（alpha **恒等于 230**，一个像素都不例外，230/255 = 0.902，省 2,772,918 字节 = 该卡 −31.04%）。

用户游戏内实测结论：**「大片半透明布料变成了不透明」** →
**这张贴图的 alpha 是真的不透明度**，230 就是那一层"半透明"的强度，不是占位值。

结论（写进代码注释反复强调）：
> **非 255 的 alpha 只要落在会被采样到的区域，就可能是真的不透明度。**

所以 `alphaNoData`（无 a<200）**不能**当作"没有 alpha 数据"的判据 —— 已从代码里删除；
lvl3/lvl4 两个实验档位一并删除，CLI `forcejpgsafe=` 的 clamp 回到 0..2。

### 47.2 matcap 例外：严格版做不到，实际用的是 0.5% 容差

§45 的圆掩罩规则本来用的是"圆内 a<200 占比 ≤0.5%"。被 47.1 打脸之后必须收紧，
于是测了三档严格度（`h_matcap_strict.py`）：

| 档位 | 判据 | 可省 |
|---|---|---|
| 档1 | 圆内 a<200 占比 ≤0.5% | 1,512,339 |
| 档2 | 圆内 **a<250** 占比 ≤0.5% | 1,512,339 |
| 档3 | 圆内**一个非 255 都没有** | **0** |

**档3 收益归零** —— 实测 9 张可放行的 matcap **全部**都在圆内有 28~16909 个非 255 像素
（圆边掩罩的抗锯齿环 + JPEG 往返抖动）。所以"绝对干净"是拿不到的。

最终采用**档2**：阈值 a&lt;250（比 a&lt;200 更严，能多拦住"整体偏暗的掩罩"），容差 0.5%。
这个容差把两类分得很开：

| | 圆内 a<250 占比 |
|---|---|
| 可放行的 8 张 matcap | 全部 **≤0.17%** |
| 真形状掩罩 `147/TexID71` / `TexID73` | **23% / 2.1%** |

### 47.3 代码上的落地：alpha 保护从「可选开关」变成「内建判据」

```csharp
// 工具自己决定格式时（auto）必须保证不丢有用的 alpha，判据只有两条：
bool alphaOk = flatAlpha                                                    // ① 逐像素全 255
            || (MatcapCircleJpeg && cls == "matcap" && alphaCircleOnly);     // ② 仅 matcap：圆掩罩
```

- ① 是原来的 `flatAlpha`，**没动**；② 是新加的内建例外，**只对 matcap 生效**。
- `protectAlpha` 形参保留但**不再参与判定**；预设里的 `protectAlpha` 字段继续读写（老预设能打开）。
- 「保护 alpha」复选框从**三个页签**全部删除
  （第一页、第二页「按部位」、第三页「单张人物卡」各有一个 —— 第一轮只删了第一页，
   靠 uitest 截图才发现另两页还在；见 47.6 教训）。

### 47.4 「全部 JPEG」不再被挡住（用户明确要求）

用户问："处理方式里的「全部 JPEG」会不会受内建判据影响？会的话改成不会。" —— **会，照搬就会**：
内建判据如果要"保护 alpha"，那显式选了 JPEG 的带 alpha 贴图还是转不了，
「全部 JPEG」这个名字又是假的（这正是原来那个复选框干的事）。

所以显式 `jpeg` 改成**无条件强制**：

```csharp
useJpg = pref == "jpeg" ? true                                            // 显式选择 = 显式承担后果
       : (pref == "png" ? false : (alphaOk && !Risky(pl)));                // auto 才受内建判据约束
```

### 47.5 实测验证（14 张服装卡，质量档）

| 臂 | 产物 | 相对 2.18 |
|---|---|---|
| 2.18 基线 | 357,634,582 | — |
| **2.19 默认**（matcap 判据内建） | **356,122,243** | 省 1,512,339（**0.42%**） |
| `matcapjpg=0`（关掉对照） | 357,634,582 | 14/14 **逐字节相同** |
| 类型表九类全部「全部 JPEG」 | **96,975,435** | **−72.88%** |

关键确认：
- `CardA.png` TexID 4（α≡230 那张）在 2.19 默认下**仍是 PNG** ✓
- 打包版 SC exe 与 dist_t 产物 SHA256 一致（8,670,786）✓
- GUI 三页 uitest 自检：`保护 alpha` 残留 **0** 处 ✓

### 47.6 教训

- **"能证明里面没有空间信息"≠"这个数不重要"**。α≡230 没有任何空间形状，
  但它是一个**全局不透明度**设置；丢掉它 = 整张材质从 90% 不透明变成完全不透明。
  这类"常数但非满值"的通道，**只能靠游戏内看**，任何静态判据都救不了。
- **一个控件改了要三页全查**：`保护 alpha` 在三个页签各有一个实例。
  第一轮只删了第一页、也只用衣服卡自检 → 全绿；换成人物卡截图才暴露第三页还在。
  **自检要覆盖所有实例所在的分支**，不能只测顺手的那条路径。
- **"严格到零"常常等于"收益归零"**：档3 相比档2 只是把阈值从 a<250、容差 0.5% 收紧到
  a≠255、容差 0，收益就从 1,512,339 掉到 **0**。定阈值前先量一下真实分布，别凭直觉取"最安全"的值。
- **名不副实的选项要修**：一个叫「全部 JPEG」的选项如果被某个隐藏开关挡住一部分贴图，
  要么改名，要么让它名副其实。用户选的是后者。
---

## 48. 2.20：按部位/人物卡页补上「锐化 + 灰度」+ 通用设置下移 + 处理不了的卡可原样复制 + 日志列优先变宽

用户一次提了四件事，都是"把批量页已有的能力搬到另外两页"。

### 48.1 引擎：把「锐化强度 / 锐化方式 / 灰度 2 通道」做成可**按部位、按贴图**指定

`Rule` 从「格式 + 最大边」扩成五项，新增的三项用哨兵值表示"跟随全局"：

```csharp
public int    SharpenPct  = -1;   // -1 = 跟随全局；0 = 这张不锐化
public string SharpenMode = "";   // "" = 跟随全局；cas | edge
public int    GrayA       = -1;   // -1 = 跟随全局；0 = 关；1 = 开
```

三个关键决定：

1. **`-1` 而不是 `0` 当"未设置"**：`0%`（不锐化）是一个**有意义的值**，
   必须能和"没设过"区分开。所有"跟随"都走 `-1`/空串，
   于是老预设/老 plan（没这三个字段）读进来天然就是"全部跟随" → 行为与以前完全一致。
2. **JSON 只在非"跟随"时才写这三个字段**（`TexTool.RuleExtraJson`），
   所以生成的 plan/预设与 2.19 逐字节相同，老工具也读得懂。
3. **合并规则**（`MergeSlotRules`，同一张贴图被多个部位共用）：
   格式取最保守、边长取最大、**锐化取最小**（谁也別把别人带进更强的锐化）、
   灰度要所有部位一致才生效，否则回到「跟随」。

### 48.2 引擎：贴图是**并行**处理的，所以这三项必须走参数，不能改静态字段

原来 `Resize()` 直接读 `Resample.SharpenK` / `SharpMode`，`PngEnc.UseGrayAlpha` 直接读静态字段。
而一张卡里的贴图是 `Parallel.For(0, total, MaxDegreeOfParallelism = texN)` 并行跑的 ——
**按贴图改静态值会串味**。所以：

- `Resize(...)` 增加 `double sharpK, double sharpKEdge, string sharpMode` 三个参数，
  调用方（`ProcessTex`）在拿到规则后一次性算好传进去；
- `grayA` 算成局部变量，替换掉三处 `PngEnc.UseGrayAlpha` 读取点；
- 注意**"纯无损快路径"**（分辨率不变 + PNG，直接对原字节做 `PngEnc.Recompress`）
  在那段代码之外还有两处 `Recompress`（GDI+ 产物的再压缩、原图的再压缩），
  它们在同一方法的外层，同名局部变量不能再声明一次，所以外层那份叫 `grayAKeep`。

### 48.3 三个页面的 UI

- **第 2 页**：一键统一行加「锐化 / 灰度」两个下拉；两个应用按钮连这两项一起设；
  **通用设置移到一键统一下面**（先一把全设、再细调）；
  部位表与贴图明细各加两列（`sh` / `ga`，下拉，首项「跟随」/「跟随部位」）。
- **第 3 页**：一键统一行同样加两个下拉（按「范围」生效）；
  一键统一下面新增**通用设置**块（锐化% / 方式 / 灰度2通道 / 细细节 / 独占保护 / 复制选项）——
  与批量页共用同一组静态设置，改一处两边同步。
  第 1 页显示时会 `SyncGlobalControls()` 按真实值刷一遍控件，
  免得出现"在第 2 页改了锐化、回第 1 页滑条还是老数值"这种假象。
- 合并到预设：`parts` / `textures` / `uniform` / `plan` 四处 JSON 都带上可选字段。

### 48.4 新选项：处理不了的卡 → 原样复制到输出目录

新增 `SkipCopy`（命令行 `copyskips=1`）。打开后，本来会"跳过"的三种情况
（与预设不是同款服装 / 预设种类不对 / 不是卡片或结构异常）改成 `File.Copy` 原文件到输出目录，
`RepackResult.Copied = true`。汇总里**单独记一笔**，不计入"成功"、不计入体积 ——
否则会出现"压了但 0%"的假象。

实测（用「兔旗袍」的预设压「Alice Rizela」，匹配度 0%）：
不勾 → 输出 0 个文件；勾上 → 输出 1 个文件 + 日志「[直接复制] …（不压缩）」。

### 48.5 日志列：拖动 + 双击复位 + **放大窗口优先变宽**

日志列的分隔条一直可以拖，这次补齐了三件事：

- 分隔条 6px → 8px、悬停提示、**双击恢复默认宽度**；
- **拖动落点修准**：`splitBegin` 原来用 `ColPixels`（控件宽度），而拖动改的是 `ColumnStyles` 的
  **列宽**，两者差一个 Margin → 每次拖动系统性地偏 5~9px。新增 `ColStylePx()`：
  列是 Absolute 就用列宽当基准。实测拖 −80 从 340 → **420**（旧代码 340 → 405）。
- **放大窗口时多出来的宽度优先给日志**（新增行为）：
  每页登记一条 `LogCol{T, LogIdx, MainCols, MainWant, MinMain, LogPref}`，
  在 `SizeChanged` 里重算 `日志 = max(用户拖过的宽度, 可用宽 − 主区首次布局宽度)`，
  并夹在 `[LogMin=180, 可用宽 − MinMain]` 之间。

  两个必须注意的点：
  1. **不能在窗体显示前测量**。首次布局期间主区可能只有几百像素宽，
     量出来的"够用宽度"会明显偏小 → 日志一上来就把宽度抢光（实测第 1 页日志直接变成 718）。
     加 `logArmed`，在 `OnShown` 之后才接管。
  2. **没显示的页不会重新布局**，自检里要逐页 `UiSelectTab` 之后再量。

  实测三页一致：

  | 窗口 | 第 1 页 日志/主区 | 第 2、3 页 日志/主区 |
  |---|---|---|
  | 1360（默认） | 334 / 984 | 322 / 962 |
  | 1700（放大） | **688** / 970 | **662** / 962 |
  | 1100（缩小） | 334 / 724 | 174 / 850 |

### 48.6 验证

| 检查 | 结果 |
|---|---|
| 默认路径语料回归（14 张卡） | **14/14 逐字节相同**（356,122,243 == 2.19）|
| 按贴图覆盖真的生效 | 写 `sh=0 / ga=1` 后 13/14 张卡产物变化（352,354,537）|
| 按部位覆盖走完整 GUI 路径 | 兔旗袍 全部跟随 2,727,079 → 不锐化+灰度开 2,379,522 |
| 复制选项 | 不勾 0 个产出 / 勾上 1 个复制产出 |
| 三页布局自检 | 「切掉」文字 0 处 |
| 拖动精度 | 拖 −80：340 → 420（期望 420）|
| 打包版 SC exe | 与开发版产物 SHA256 一致 |

### 48.7 教训

- **"未设置"和"设成 0"必须能区分**。用 `-1`/空串当"跟随"哨兵，
  才让"这张不锐化"这种明确选择存在；否则老预设一读进来就会把默认值当成用户意愿
  （2.18 的 `sharpenModeExplicit` 是同一个坑的另一面）。
- **并行处理 + 静态全局设置 = 串味**。加"按 X 单独设置"的功能时，
  第一件事是看这个设置是**静态字段读的**还是**参数传的** —— 前者必须先改成参数。
- **为了一个赋值多声明一次同名变量会让 C# 报错**，得换个名字（`grayAKeep`）——
  这其实是个好信号：说明那段代码跨度大、有"外层也要用"的隐含依赖，值得停下来看清楚。
- **"首帧测量"几乎总是错的**。任何"按当前布局决定后续布局"的逻辑，
  都要等布局稳定（`OnShown`）之后再开始，并且**只测量一次、之后不再改基准**。
- **拖动偏移的根因是两套度量混用**（控件宽度 vs 列宽），
  这类偏差不会报错、只会每次都差一点，必须靠"拖固定距离、看落点"的自动化自检才抓得住。
---

## 49. 2.21：一键统一收敛为"子项都在里面"；删掉通用设置；日志优先变宽只留第 1 页

用户的四条要求，全在第 2、3 页上。

### 49.1 一键统一 = 所有处理方式的入口（第 2、3 页）

第 2 页的「一键统一」从**左列**搬到**中间列、部位一览的上方**（照第 3 页的 `gWrap` 结构）——
左列只有 340px，塞不下"处理方式/最大边/锐化/灰度2通道/保细节/独占保护"这么多项。
现在两页的一键统一里都有：

```
[一键统一]  范围(仅第3页) | 处理方式 [..] | 最大边 [..] | 锐化 [..] | 灰度2通道 [..]
            ☑ 贴图细节保护   ☑ 独占贴图保护 [上限 ..]   [一键统一]/[应用到所有部位][应用到本部位全部贴图]
```

两类语义分开并在说明行里写明：
- 处理方式 / 最大边 / 锐化 / 灰度2通道 → **由按钮套到部位或贴图**（单张贴图里的「跟随」= 用这里的值）；
- 贴图细节保护 / 独占贴图保护 → **整卡开关，勾了立刻生效**，不经过按钮。

名称统一：表头「方式/边长」→「处理方式/最大边」；列头「灰度」→「灰度2通道」。

### 49.2 「标签 + 选择框」必须成对换行

用 `Pair(标签, 控件)` / `Pair2(控件)` 把每个"文字 + 选择框"包成一个 `FlowLayoutPanel`
（`WrapContents=false`，Margin 小），外层 `WrapContents=true` 的容器换行时**整对一起换**。
不然会出现"文字留在上一行、选择框掉到下一行"这种看着像孤儿的情况。
实测三页 `UiClippedText` 的「切掉」计数为 0。

### 49.3 删掉「通用设置」（第 2、3 页）

不匹配的服装只剩两种结局，默认是第一种：

| 情况 | 行为 |
|---|---|
| 默认（不勾） | 与预设不是同款 → **既不压缩、也不复制**（跳过） |
| 勾「不匹配的服装直接复制到输出目录（不压缩）」 | 原样复制到输出目录 |

第 3 页连这个勾选框也不要（人物卡不做同款检测）。

代码层面删掉 `cbUniformFmt` / `cbUniformSize` / `chkForce` 与 `UiUniform` / `UiForce`，
`ApplyUniformToPreset()` 变成空实现（`pf.Uniform = null; pf.ForceUniform = false;`）——
方法保留只是不想动调用点。**预设/计划文件里的 `uniform`/`forceUniform` 字段仍可读**，
CLI `force=1` 仍可用，只是界面不再提供入口。
顺手把跳过提示里那句「勾上『不匹配服装使用通用设置压缩』」（控件已不存在）改成
「勾上『不匹配的服装直接复制到输出目录』」。

### 49.4 「放大窗口优先扩大日志框」只给第 1 页

去掉第 2、3 页的 `RegisterLogCol`（`HookLogSplitter` 保留 → 仍然能左右拖、双击复位）。
于是放大窗口时，第 2/3 页多出来的宽度给**表格**：

| 窗口 | 第 1 页 日志/主区 | 第 2 页 部位表/日志 | 第 3 页 部位表/日志 |
|---|---|---|---|
| 1360 | 334 / 984 | 318 / 314 | 320 / 314 |
| 1700 | **688** / 970 | **488** / 314 | **490** / 314 |
| 1100 | 334 / 724 | 188 / 314 | 190 / 314 |

### 49.5 关于「灰度」与「灰度用 2 通道」

**是同一件事**（`PngEnc.UseGrayAlpha`）：R==G==B 的贴图只存一个通道，省掉重复的两份，
解出来像素逐位不变。以前表格列头简写成「灰度」、第 1 页写成「灰度用 2 通道」，
容易被当成两件事，现在统一叫「灰度2通道」并加了悬停说明。

### 49.6 验证

| 检查 | 结果 |
|---|---|
| 默认路径语料回归 | 14/14 逐字节相同（356,122,243） |
| 按贴图覆盖生效 | 13/14 张卡产物变化 |
| 按部位覆盖走完整 GUI 路径 | 2,727,079 → 2,379,522 |
| 不匹配服装默认行为 | 输出 0 个文件（不压缩、不复制） |
| 勾上复制 | 输出 1 个文件 + 「[直接复制]」 |
| 三页「文字被截断」 | 0 处 |
| 放大窗口的宽度归属 | 第 1 页给日志；第 2/3 页给表格 |
| 打包版 SC exe | 与开发版产物 SHA256 一致 |

### 49.7 教训

- **"把选项放进一个块"要先看那块有多宽**。第 2 页左列只有 340px，
  硬塞 6 组"标签+下拉"必然换行成一片；搬到中间列（≈600px 起、放大还会更宽）才对。
- **成对换行要显式做**：FlowLayoutPanel 默认按控件换行，
  把"文字 + 选择框"包成子面板是唯一可靠的办法（也是唯一不用手算宽度的办法）。
- **删功能要连"提示文案"一起删**。`chkForce` 删掉之后，
  跳过日志里还留着"勾上『不匹配服装使用通用设置压缩』"——一个已经不存在的控件。
  这类"指向已删控件的引导语"只有靠搜索控件名才查得出来。
- **一个行为要按页开关**（日志优先变宽）：把它做成按页登记的规则而不是写死，
  这样"只对第 1 页生效"就是删掉两行注册代码的事。
---

## 50. 2.22：一键统一再加"锐化算法 + 两个保护开关"；只压一件 vs 整个目录；按钮做大

### 50.1 第 2 页新增「将压缩应用到目录下的所有相似服装」

以前点「▶ 按部位压缩」只压当前这一件。现在多了一个勾选框（默认关）：

| 勾选状态 | 行为 |
|---|---|
| 不勾（默认） | 只压缩当前这一件 |
| 勾上 | 把当前设置当预设，套到**这张卡所在目录**里与它同款的卡上（chkSub 决定含不含子文件夹） |

实现上就是复用已有的"批量套用"那条路：`DoPartsRun()` 里如果勾了就转到
`DoBatchPresetTo(卡所在目录)`；把原来那个自检专用的重复实现（`UiBatchPreset` 里另抄了一份）
删掉，统一成：

```
BatchPresetUi()              → 弹文件夹选择框 → BatchPresetTo(pf, inDir, od)
DoBatchPresetTo(dir, confirm) → 目录=卡所在目录，输出=界面上的输出目录 → BatchPresetTo(...)
BatchPresetTo(pf, inDir, od)  → 真正干活（串行扫卡 + 每张 Repack）
```

**顺带修了一处分类错误**：批量套用时 `Rc == 6`（不是同款服装）以前被算成**失败**。
不同款是**预期内**的结果，现在算**跳过**，并且汇总里单列"原样复制 N 张"。

实测（目录里放 2 份兔旗袍 + 1 张别的衣服）：

| 设置 | 产出 |
|---|---|
| 只压一件 | 1 个文件 |
| 套到所有相似服装，不复制 | **2** 个（两份同款都压，别的那张跳过，失败 0） |
| 套到所有相似服装 + 复制不匹配 | **3** 个（2 压 + 1 原样复制，体积不变） |

### 50.2 「不匹配的直接复制」受「相似服装」管

`SyncAllSimilar()`：没勾"相似服装"时把 `chkCopySkip.Enabled = false` 并强制关掉它 ——
只压一件时不存在"不匹配的卡"，那个勾选框留在那里只会误导。

### 50.3 锐化算法也能按部位/按贴图选了

一键统一里「锐化」旁边加「锐化算法」（跟随 / 对比度自适应 / 边缘感知），
两张表各加一列 `shm`。`Rule.SharpenMode` 引擎层早就支持，这次只是把它接出来。
实测：`g3uni=全部组|自动|1024|100%|边缘感知|开` → 组·部位表 157 行全部变成
`自动|1024|100%|边缘感知|开`，并一路传到编码器。

### 50.4 「独占贴图保护 + 上限」绑成一组；上限多一项 1024

```
OwnMaxItems = { "放宽到 4096", "放宽到 2048", "放宽到 1024", "原尺寸不降" }
OwnMaxVals  = { 4096, 2048, 1024, 0 }
```
`OwnMaxIndex` 改成按值查表（老值 0/2048/4096 → 直接命中；其它值落到最近的档）。
界面上「☑ 独占贴图保护」和「上限 […]」放进**同一个 Pair 面板**，换行时不会分家。

### 50.5 第 3 页：组过滤加「全选 / 全不选」

`G3GroupAllEvery(bool)`：不管「组过滤」显示哪一组，一次改全部行。
和已有的「当前组全选/全不选」并列成四个按钮。

### 50.6 执行按钮做大

第 2/3 页的运行按钮改成和第 1 页一个规格：
`Font = 9.75f Bold`、`Padding(22,9,22,9)`、文本加 `▶  ` 前缀。

### 50.7 抓到并修好的"隐形截断"

第 2、3 页那句说明文字（AutoSize 的 Label **不会折行**）超出了容器宽度被**直接截掉**：
第 2 页需要 602px 而中间列只有 301px。**这个截断在 2.21 就存在**，之前的自检只查了第 1 页
（`rects=1` 默认 dump 第 0 页的控件树），所以一直没看见。
现在按 22 字左右手动断行 + 三页都查 `[截断]`，结果为 0。

### 50.8 教训

- **"只压一件 / 压一整个目录"是语义分叉，不是参数**：与其在单卡路径里塞一个 if，
  不如让它转到已经存在的批量路径上（少一份重复实现）。这次顺手删掉了那份重复的自检实现 ——
  重复实现最大的代价就是**两边的修正只落在一处**（比如 `r.Copied` 只在新的那边处理了）。
- **`AutoSize=true` 的 Label 不会折行，只会被截断**。长文案要么手动断行，
  要么设 `MaximumSize`；而且**自检要覆盖到那句文案所在的页**，否则永远发现不了。
- **默认值要跟"这个选项有没有意义"绑定**：只压一件时不匹配复制没有意义，
  就该置灰 + 强制关掉，而不是留着让用户以为它在起作用。
- **自检输出要看对位置**：同一个 dump 在流程里会出现两次（前 / 后），
  取第一个匹配就会看到"没生效"的假象 —— 这次在这个坑上连栽了两次。
---

## 51. 2.23：默认预设；人物卡页只处理一张；灰度两态；界面归位；"无法处理目标"文案与位置

### 51.1 「将当前预设设为默认」

预设行右侧新增勾选框。实现刻意不用注册表/用户目录：

```
preset\<kind>\.default     ← 一行文件名（不含路径）
```

- `PresetIo.GetDefaultPreset(kind)` 读它；里面写的文件已经不在 → 当作没设。
- `PreReload()` 的选中优先级：**记忆的当前选中（keepName）> 默认标记 > 「仅优化」**。
  记忆优先是有意的：用户刚手动切过一次，不该因为存在默认标记就被弹回去。
- `SyncPreDefaultCheck()` 让勾选框跟着下拉框走（选中的正好是默认那个才勾上）。
- 删除 `.default` 文件 = 取消，行为与取消勾选一致。

实测：选中「质量」→ 勾上 → **新进程启动**日志为「套用预设：…[preset]质量_内置默认.json」；
取消后新进程 → 回到「仅优化」。

### 51.2 第 3 页：删掉「拖入目录时含子文件夹」+ 只处理一张卡

- `chkG3Sub` 整个删除（连同 UI 与枚举调用）。
- 拖入多个文件/整个目录时：只取第一张，目录**不递归**，并在日志说明"已只读取第一张"。
- `G3Load()` 里再兜一道：`g3Cards.Count > 1` → `RemoveRange` 只留第一张。
- 要一次压多张人物卡 → 用第 1 页批量压缩。

### 51.3 一键统一里的「灰度2通道」改成两态

新增 `UniGrayItems = { "开", "关" }`，只给两页的一键统一用；
表格列仍用三态的 `GrayItems`（含「跟随」）。`Rule.GrayA` 的 -1 语义不变 ——
一键统一不再产生 -1，"清掉某处覆盖"只能去表格那一格选「跟随」。

### 51.4 界面归位（第 1 页）

- **「统一设置」挪进全局选项块的最上面**（`pOpt.Controls.SetChildIndex(pUni1, 0)`），
  位置从"预设行下面、类型表下方"变成**在「JPEG 质量」之上**。
- **「无法处理目标一同不压缩并复制进输出目录」**
  （文案按用户给的原话）并到「覆盖同名」后面，不再单独占一行。

### 51.5 「相似服装」的判据（查证记录）

```csharp
// 签名 = 部位 | 材质名(去副本后缀) | 属性名
public static string BindingSig(int slotKey, string material, string prop)
    => Card.SlotKey2Text(slotKey) + "|" + NormMaterial(material) + "|" + (prop ?? "");

// 预设记住的签名集：优先整卡指纹 SigSet，老预设退回单张贴图规则里的签名
public HashSet<string> AllSigs()

// 相似度 = 预设签名里能在这张卡上找到的比例
public double Score(HashSet<string> cardSigs) => (double)hit / mine.Count;
```

判定：`Score < preset.MinMatch`（默认 **0.5**）→ 不是同款。
实测：同卡副本 100%；兔旗袍 vs Alice Rizela 0.0%。人物卡不做同款检测。

### 51.6 教训

- **"默认项"要能被显式清掉，且清掉后行为可预测**：优先级写成
  「用户当前选择 > 默认标记 > 内置默认」，这样"取消默认"不会立刻把界面弹走，
  但下一个进程一定回到内置默认 —— 两头都不意外。
- **删功能要顺着引用删到干净**：删 `chkG3Sub` 时带出 `rSub` 面板的引用（编译错误才暴露），
  这类"控件 → 面板 → 布局行"的三级引用在 WinForms 里很常见。
- **验证脚本本身也会说谎**：`compress <in> <out>` 的 out 是**输出文件路径**，
  我一直按目录传 —— 结果在目录里生成了一个**无扩展名的文件**，
  而 `Get-ChildItem <文件> -Filter *.png` 照样把它返回（Filter 对非目录路径不生效），
  于是"冒烟测试通过"是假的。这次顺手把 `perf\v219..v222` 里那些无扩展名残留都删了，
  并把冒烟改成传明确的输出文件名 + SHA256 对比。
---

## 52. 2.24：读卡提速（整读 2~3 遍 → 1 遍）+ 按钮样式统一 + 行高锁死

### 52.1 先量再改：读卡到底慢在哪

加了 `KOITEX_SCANTIME=1` 分阶段计时（`ScanParts` 内部）与整读计数器
（`TexTool.FullReads / FullReadBytes`、`Card.KeyScanCount / KeyScanMs`），
拿真卡实测：

| 卡 | 体积 | 读盘 | 解析字典 | 判定卡种 | 属性表 | 签名 | 收尾(逐张贴图取字节) | 合计 |
|---|---|---|---|---|---|---|---|---|
| 兔旗袍 | 21.5 MB | 7ms | 2ms | 1ms | 4ms | 3ms | 16ms | ~34ms |
| natsu（人物卡） | 270.8 MB | 65ms | 4ms | 1ms | 51ms | 5ms | 134ms | ~260ms |
| DX4（人物卡） | 230.0 MB | 55ms | 3ms | 1ms | 15ms | 4ms | 108ms | ~190ms |

结论：**开销几乎全在"把整张卡读进内存"和"逐张贴图取字节"**，算法本身没有问题。
所以慢的根因不是复杂度，而是**同一张卡被整读了好几遍**。

（顺带纠正一处我先前的误判：我原本猜"整文件键扫描 5~8 遍 × 270MB = 1~2GB"，
实测键名都落在卡头几百 KB 内 —— PNG 缩略图 IEND 在 140~152 KB ——
8 次扫描合计只有 16~47ms。注释里已经改成实测口径。）

### 52.2 去掉的重复读（每一条都是"少读一整张卡"）

| # | 原来 | 现在 | 省 |
|---|---|---|---|
| 1 | `LoadParts`/`G3Load`：`ScanParts` 读一遍，紧接 `Card.ReadInfo(Peek(path))` 再整读一遍 | `PartsScan.Raw` 把已读字节带回来给 `ReadInfo` | 1 整读 |
| 2 | 点一行贴图预览：`TextureBytes(path, id)` → `ReadAllCached` 再整读一遍 | 新增 `TextureBytes(byte[], id)`，直接用内存里那份卡字节 | 1 整读 |
| 3 | `AutoExpandGroupFilter`：**最多 8 张卡整读**只为判断卡种 | 新增 `PeekHead`（前 1MB）+ `IsCharaCardFast` | 8 整读 → 8 MB |
| 4 | 批量扫描每张卡的卡名：`ReadInfo(Peek(...))` | `ReadInfo(PeekHead(...) ?? Peek(...))` | 每卡 1 整读 → 1 MB |
| 5 | `FindKey` 每次都从 0 扫；`IndexOf` 手写逐字节 | `FindKeyCached`（ConditionalWeakTable 挂在 byte[] 上）+ `MemoryExtensions.IndexOf` | 重复扫描 |

实测效果（GUI 自检全程统计）：

```
第 2 页 衣服卡：整读 1 次 / 21.5 MB（改前 2 次 / 43.0 MB）
第 3 页 人物卡：整读 1 次 / 270.8 MB（改前 2~3 次）
```

### 52.3 仍然没解决的（按性价比排序，已写进使用说明）

1. **读卡跑在界面线程上** → 那 260ms（慢盘上 3~10s）窗口是冻住的，
   连"正在读取…"都刷不出来。这是**体感**问题的主因，也是下一轮最该做的。
2. **没有记住扫描结果**：切页签/重复读同一张卡要重跑一遍。
3. **批量扫描是串行的**（压缩早就并行了，只有扫描这一段没并行）。
4. 卡放在机械盘/网盘/云同步目录 —— 拷到本地 SSD 比任何代码优化都有效。

### 52.4 界面

- `MakeMainButton(btn, text, baseFont)`：三页主操作按钮统一走它
  （字号 +3.5、加粗、Padding 26/10、▶ 前缀）。以前三处各写一套，已经在慢慢走偏。
- 四张表 `AllowUserToResizeRows = false`：行高不许拖（原来鼠标移到行线上会变上下箭头，容易误触）。

### 52.5 教训

- **先量再改**。我一开始把"读卡慢"归因到"键扫描扫了整文件好几遍"，
  加了计数才发现键名都在卡头 150KB 内、8 次扫描共 16ms；真正的大头是整读遍数与逐张贴图取字节。
  如果按错误归因去做优化（比如把键扫描改得更花哨），收益接近 0。
- **"读了几遍"要能被数出来**。加一个 `FullReads/FullReadBytes` 计数器，
  比盯代码找重复调用可靠得多 —— 这次 4 处重复读有 3 处是计数器帮我定位的。
- **同一份数据在一条流程里被读多次，通常是因为接口没把数据传下去**：
  `ScanParts` 明明读到了字节，却只返回解析结果，于是下一个要字节的环节只能再读一遍。
  把"已读到的原始数据"顺着返回值带下去，是最省事也最有效的一类优化。
- **资源类的旋钮要统一出方法**：`MakeMainButton` 之前是三处各写一遍的样子代码，
  这种"看着一样"的重复最危险 —— 改的时候漏一处，界面就开始不一致。
---

## 53. 2.25：读卡真凶是"预览反复解码"（30s → 2.5s）+ 后台读卡 + 扫描缓存/并行 + "跟随"归置

### 53.1 上一轮的诊断不完整：整读次数不是体感的主因

2.24 把整读从 2~3 遍降到 1 遍，但**体感几乎没变**。继续加计时才拿到真相：

| 阶段 | 耗时 |
|---|---|
| 读 22MB 衣服卡本体（ScanParts） | 34 ms |
| "选中这张卡"界面完整就绪 | **28,000 ms** |

差 800 倍。**27.9 秒全在预览缩略图上**：表格每填一次就触发 `SelectionChanged`，
每次都把当前选中的贴图**整张解码**（4096² PNG 一次 100~300ms）；
填表 + 刷新明细 + 每次改下拉框（`CellValueChanged → FillTexList`）叠加几十次 = 几十秒。

定位手法：加 `KOITEX_NOPREVIEW=1` 开关跳过预览解码，同一条流程
**29.7s → 2.4s**，一次复现就锁定了。

### 53.2 两道修

1. **网格刷新期间不预览**：`FillPartsGrid` / `FillTexList` / `G3Fill` / `G3FillTex`
   外面包 `suppressPreview`，刷新完只 `UpdatePreview` 一次。
2. **缩略图缓存**：key = `卡路径|TexID`，存**已缩放好的**小图（≤16 张，换卡清空）。
   以前同一张图被解码几十遍，现在一遍。

实测（同一条自检流程）：

| 页面 | 改前 | 改后 |
|---|---|---|
| 第 2 页 衣服卡 22MB | 29,657 ms（读卡阶段 27,990ms） | **2,581 ms**（读卡阶段 925ms） |
| 第 3 页 人物卡 270MB | ~30 s 量级 | **2,308 ms**（读卡阶段 704ms） |

### 53.3 另外三项

- **读卡搬到后台线程**（`LoadParts` / `G3Load`）：先刷状态再丢后台，读完经 Post/Drain 回主线程填表。
  以前是同步调用，那几百毫秒到几秒里窗口完全冻住、状态栏都刷不出来。
  自检里加了 `UiPartsBusy` / `UiG3Busy` 等待，否则断言会读到半成品状态。
- **扫描结果缓存**：`ScanPartsCached` 按「路径+mtime+size」缓存 `PartsScan`，只留最近 2 条；
  为省内存只有最新那条保留 `Raw`（旧条目仍可用于表格/规则，预览会退回按路径读一次）。
- **批量扫描并行**：原来 14 张卡串行 ≈4s；现在 `Parallel.For`（上限 4，`KOITEX_SCANJOBS=N`），
  日志仍按原顺序串行回报，输出顺序不变。`TexTool.Scan` 用的都是局部状态 + 线程安全的键缓存，
  并行安全。

### 53.4 "跟随"的最终归置（用户要求）

把三套选项显式分开，避免混用：

| 用途 | 数组 | 有没有"跟随" |
|---|---|---|
| 一键统一（设值） | `SharpenItems` / `GrayItems` / `ShModeItems` / `G3FmtItems` / `G3SizeItems` | **没有** |
| 部位一览 / 组·部位表 | 同上（共用） | **没有**（默认 100% / 对比度自适应 / 开）|
| 选中部位的贴图明细 | `TexSharpenItems` / `TexGrayItems` / `TexShModeItems` / `TexFmtItems` / `TexSizeItems` | **有**（第一项「跟随部位」）|

取值映射的"取不到就回落"也一并改了：`SharpenValToItem(-1)` → `"100%"`（不再返回 `"跟随"`），
`GrayValToItem(-1)` → `"开"`，`ShModeValToItem("")` → `"对比度自适应"`。
这几个默认值正好等于全局默认，所以**按部位压缩的产物与上一版逐字节相同**（实测 2,727,079）。

引擎侧 `Rule` 仍支持跟随（`SharpenPct=-1` / `SharpenMode=""` / `GrayA=-1`），老预设读进来照旧。

### 53.5 教训

- **"修了一半没效果"要继续量，不要以为方向对了就够了**。2.24 确实少读了 1~2 遍整卡，
  但那 130ms 和 28s 相比可以忽略 —— 真正的热点在另一层（UI 事件风暴）。
- **加一个"跳过某阶段"的开关是定位利器**。`KOITEX_NOPREVIEW=1` 让"预览"和"读卡"的账分开算，
  一次就把 28s 归给了预览。比读代码猜快得多。
- **DataGridView 的 `CellValueChanged` / `SelectionChanged` 会在填表时疯狂触发**，
  凡是"事件里干重活"的地方都要有"批量更新期间抑制"的开关 + "同一份结果缓存"。
  这个模式在本项目已经踩过两次（这次是预览解码，上次是自动扫描）。
- **把 UI 事件里的重活缓存住，收益往往比优化算法大一个数量级**：这次一行缓存 30s → 2.5s。
---

## 54. 2.26 修复：预览第二次点同一张贴图报「Parameter is not valid」（2.25 自己引入的）

### 54.1 现象与第一步排查（先证明"图片没问题"）

用户在 `CardA.png_DX4 FOR LORA_DX4.png`（230 MB 人物卡）上，
贴图明细里**部分**贴图预览报「预览失败：Parameter is not valid.」。

先排除数据问题的三件事：

1. 用工具自己的扫描：68 张贴图全部正常识别（无报错，尺寸也都读得出）。
2. 逐张导出原始字节，走一遍 PNG 结构（签名 / IHDR / IDAT / IEND / CRC / 是否截断）：
   **68/68 结构完好**，PIL 也全部解得开。
3. 把 68 张写成独立文件，用**同一套 GDI+**（`System.Drawing.Image::FromStream`）逐张解：
   **68/68 成功、0 失败**。

→ 数据没问题，问题在"我们怎么用那个 GDI+ 对象"。

### 54.2 复现与真因

用自检钩子复现：`uitest <卡> tab3=1 g3prev=3` → 三行全部 `预览失败：Parameter is not valid.`

真因是 2.25 加缩略图缓存时留下的**共享 Bitmap 双重释放**：

```csharp
var old = pic.Image;
pic.Image = null;
if (old != null) old.Dispose();          // ← 这里释放的是"上一次显示的那张"
...
pic.Image = small;
CacheThumb(ck, small);                   // ← 2.25：缓存和界面**共用同一个 Bitmap**
```

第二次预览同一张贴图时：`old = pic.Image` 恰好就是缓存里那个对象 → `Dispose()` 把它释放掉 →
再从缓存 `Clone()` 一个**已释放**的 Bitmap → GDI+ 抛 `ArgumentException: Parameter is not valid`。

这也解释了为什么是"**部分**贴图"：只有被预览过两次以上的那张才会炸，其余的照常。

### 54.3 修法

缓存里存**独立副本**，界面显示的那份与缓存彻底分开：

```csharp
pic.Image = small;
CacheThumb(ck, (Bitmap)small.Clone());   // 缓存自己留一份
```

命中缓存时也 clone 一份给界面（`pic.Image = (Bitmap)hit.Clone()`），
并把 `CacheThumb` 改成"同名键换新前先释放旧副本"。这样 `old.Dispose()` 永远只释放界面那份。

### 54.4 验证

| 检查 | 结果 |
|---|---|
| 同一张贴图连续预览（第 2、3 页多次） | 全部正常，无该报错 |
| DX4 卡整体压缩一遍 | 68 张贴图**全部解码成功**、校验通过（241.2 MB → 79.5 MB，33.0%）|
| 三页自检「截断/预览失败」计数 | 0 |
| 默认路径语料回归 | 14/14 逐字节相同 |

### 54.5 教训

- **缓存"显示用的对象"是经典陷阱**：任何"既显示又缓存"的 `IDisposable`，
  都必须让两处各持一份（或干脆不缓存可显示对象）。这次是 `Bitmap`，
  换成 `Image`/`Stream`/`Font` 一样会踩。
- **"部分 X 坏了"要先问"是不是第二次才坏"**：只有被重复访问的对象才会暴露双重释放，
  所以症状看起来像"随机坏了几张"。复现时**故意对同一项做两次**，是这类 bug 的关键手法。
- **先证明数据是好的，再查代码**：导出 + 独立解码 + 结构校验三步走完，
  把"贴图损坏"这个方向一次性排掉，剩下就只有"对象的生命周期"这一种可能。
- 顺带说明：这类 bug 只在**新加的缓存路径**上出现，所以"加缓存提性能"的改动
  必须连带测"同一资源重复访问"，否则很容易只测了第一次访问就以为通过了。

---

## 55. 2.27：选中贴图的「⛶ 独立窗口」放大预览

### 55.1 需求

> 加入一个按钮，能够一键放大选中部位贴图的预览图并置于一个独立的悬浮窗中，
> 鼠标滚轮能够放大或者缩小，并且切换其他贴图时这个窗口也会跟着切换贴图。

### 55.2 实现

新增 `src/PreviewForm.cs`（约 200 行，自绘）：

| 行为 | 实现 |
|---|---|
| 滚轮缩放 | `MouseWheel` → `ZoomAt(光标位置, delta)`，每格 ×1.15，夹在 0.05~16 |
| 缩放锚点 | 反解光标下的图像坐标 `ix=(at.X-off.X)/oldZoom`，缩放后回算 `off`，所以光标下的那个像素不动 |
| 拖动平移 | `MouseDown/Move/Up` 记起点与 `off`，位移直接加到 `off` |
| 适应窗口 / 100% | 双击、按 0/F = fit；按 1 = 100% |
| 采样方式 | `_zoom>=2` 用 `NearestNeighbor`（看真实像素），否则 `HighQualityBicubic`（缩小时不锯齿） |
| 底栏 | 上面一行说明（`AutoEllipsis` 单行省略），下面一行快捷键提示；分包在 `Dock=Bottom` 的 Panel 里 |

主界面（`MainForm.cs` / `MainFormChara.cs`）两处接上：

```csharp
// 预览框右上角钉一个按钮
btnBigP2.Anchor = AnchorStyles.Top | AnchorStyles.Right;
btnBigP2.Location = new Point(prevP2.ClientSize.Width - btnBigP2.Width - 6, 6);
btnBigP2.BringToFront();                       // ⚠ 见 55.4
btnBigP2.Click += delegate { OpenBigPreview(picP2, gridTex, txtP2Card.Text.Trim()); };
```

`UpdatePreview()` 里加了「悬浮窗开着就走另一条路」：

```csharp
if (BigOpen) { prevBig.SetImage(img, BigCaption(grid, cardPath, cap)); bigKey = ck; }
else img.Dispose();                            // 不开悬浮窗时照旧立刻释放
```

- **所有权转移**：整图交给悬浮窗，由它释放上一张（`SetImage` 里 `Dispose` 旧的），
  主界面那份缩略图是另画的，两边不共用对象。
- **不开悬浮窗时行为不变**：仍然立刻 `Dispose` 整图，只留缩略图。
- **绕过缩略图缓存**：缓存里只有降采样的小图，悬浮窗要整图，所以 `BigOpen` 时
  跳过 `thumbCache` 那条捷径（这是**刻意**的，不是漏写）。

### 55.3 自动跟随 + 底部说明行

`UpdatePreview` 是所有换图路径的唯一出口（`SelectionChanged`、换部位 `FillTexList`、
换组 `G3FillTex`、按钮点击都汇到这里），所以在它里面喂悬浮窗就等于"跟着换"。

底部说明行比小预览多带三项 —— 独立窗口已经离开主界面表格，光一个 TexID 认不出是哪张：

```
TexID 47   2048×2048   2.64 MB   PNG　类型=normal　共用：与 角色本体 · 身体/脸/头发/眼睛 + …　卡=Koikatu_F_…DX4.png
```

（`共用情况` 超过 44 字截断加省略号。）

### 55.4 踩到的坑：浮在 Dock=Fill 控件上的按钮会被盖住

- `prevP2` 里 `picP2` 是 `Dock=Fill`，按钮是**兄弟控件**且后 `Add`。
  WinForms 里后 Add 的控件 z 序在后 → 按钮被 PictureBox 整个盖住，看不见也点不到。
  只靠 `Resize` 里 `BringToFront()` 不够（首次布局不一定来得及），必须在 Add 之后立刻调一次。
- **`DrawToBitmap` 看不到这个按钮**：`UiSnap` 出来的界面图里预览框右上角是空的，
  但真实窗口截图（`PrintWindow`）里按钮清晰可见。查"控件到底显示没有"这类问题，
  不能只看 `DrawToBitmap`。
- 为此加了自检钩子 `UiSnapBig(path)`（把悬浮窗画成 PNG）和 `uitest ... hold=毫秒`
  （把窗口留在屏幕上跑消息循环），配套脚本 `_work/kkcard_exe/capwin.ps1`：
  按 PID 枚举顶层窗口 → `PrintWindow` 存 PNG。这次就是靠它确认两个页面的按钮都在。

### 55.5 验证

| 检查 | 结果 |
|---|---|
| 衣服卡 4096² maintex，滚轮 3 格 | 100% → 115% → 132% → 152%，关闭正常 |
| 明细换行（TexID 3 → 2） | 悬浮窗跟着换，两张都是 4096² 原图 |
| 人物卡 68 张，明细 3 行 | TexID 47(2048²) → 48(512²) → 46(1024²)，逐行跟随 |
| 换「组·部位」（角色本体 → 换装1 袜子） | 悬浮窗跟着换成新组第一张（TexID 47） |
| 真实窗口截图 | 第 2、3 页按钮均可见、右上角对齐 |
| 默认路径语料回归 | 14/14 逐字节相同（改动没碰压缩链路） |

---

## 56. 实测：能不能通过服装卡读到模型的 UV？

**结论分两种读法，答案不一样：**

### 56.1 如果指的是「UV 空间里的贴图」（也就是贴图本身）→ 能

衣服卡里的贴图就是按 UV 铺开的那张图（u=x、v=y 直接对应），
这张工具压的就是它 —— 第 55 节的放大预览看到的也正是 UV 空间里的纹理。

### 56.2 如果指的是「模型每个顶点的 UV 坐标 / UV 布局」→ 不能

**卡里根本没有 mesh**。14 张语料卡逐字节查过，证据如下。

**（1）服装卡的结构（以 `兔旗袍.png` 为例，22,540,682 字节）**

```
0 .. 141,006        卡面 PNG
141,006 .. 141,046  头：int32(100) + "【KoiKatuClothes】" + version + name
141,046 .. 145,885  元数据 msgpack，只有 6 个顶层键：
                    version / parts(9) / subPartsId / hideBraOpt / hideShortsOpt / ExtendedSaveData
之后                 MaterialEditor 等插件数据（含 TextureDictionary）
```

`parts[]` 每项的键只有：`id`、`colorInfo[]`（`baseColor` / `pattern` / `patternColor` / `tiling`）、
`emblemeId`、`emblemeId2`、`hideOpt`、`sleevesType` —— 没有任何 mesh / vertex / uv 字段。

**（2）插件块里有什么（`Alice Rizela.png`）**

```
TextureDictionary          3 项：贴图名 → 原始 PNG 字节
MaterialTexturePropertyList 9 条：{ObjectType, CoordinateIndex, Slot, MaterialName,
                                  Property='MainTex', TexID, Offset, OffsetOriginal,
                                  Scale, ScaleOriginal}
MaterialShaderList          3 条：ShaderName / ShaderNameOriginal / RenderQueue
MaterialFloatPropertyList / MaterialColorPropertyList / MaterialCopyList
com.deathweasel.bepinex.pushup      PushupCoordinate_BraData/TopData（各 319 B）
com.bepis.sideloader…autoresolver   info[]：每个 mod 约 150 B 的解析结果
moreAccessories                     additionalAccessories XML
PluginListTool                      PluginList bin（14 KB）+ LastSaved 时间
```

**（3）唯一和 UV 沾边的数据是"UV 缩放/偏移"，不是 UV 布局**

- `MaterialTexturePropertyList` 里的 `Offset` / `Scale`（外加 `*Original`）= 材质的
  贴图平铺/位移覆盖（对应 shader 的 `_MainTex_ST`）。实测这张卡这几项都是 `None`。
- 元数据 `colorInfo[].tiling` 也是同一类东西（变换参数）。
- 两者都只是"拿这张 UV 图怎么采样"，**不含任何顶点级 UV 坐标**。

**（4）"Mesh"/"UV0"/"uv2"/"UVW" 的字节命中全是噪声**

用逐字节搜索 14 张卡，并对同长度的随机 ASCII 串做噪声基线：

- `Mesh` 命中（如 `CardA.png` @171,086 / @171,112）
  落在**插件列表文本**里：旁边就是 `GUID`、`Grey.MeshExporter.KK`、`Version` ——
  是一个**插件名**（MeshExporter 是"把游戏里的 mesh 导出成文件"的插件），不是 mesh 数据。
- `UV0` / `uv2` / `UVW` 的命中**全部落在贴图 blob 内部**（PNG/JPEG 压缩流里的巧合字节），
  例：`UV0 @19,363,624` 落在 `[18,609,098, 20,235,826)` 这段贴图 blob 里。
- 4 个同长度控制串（`Qz7Xk2` / `Wm3Pv9` / `Tx8Lr4` / `Zb5Nq1`）在 14 张卡里命中 **0** 次 ——
  说明这些短 ASCII 在几十 MB 数据里本来就可能偶然出现，不能拿"搜到了 XYZ"当证据。
- `UnityFS` / `CAB-` / `PK`：**14 张卡里一次都没有** → 卡里没有内嵌 assetbundle。

**（5）那 UV 从哪来**

在 mod 自己的 assetbundle 里：`.zipmod` → `abdata/chara/*.unity3d`，
里面是 `Mesh` + `SkinnedMeshRenderer`（UV0/UV1 跟着 mesh 走）。
用 AssetStudio / uTinyRipper 打开导成 FBX，UV 就在里面 ——
之前盘点 SY 服装资产时走的就是这条路（见 §37 前后）。

**回到工具上**：这个工具做的是"改卡里的贴图字节"，从头到尾不需要 mesh，也不会碰 mesh；
所以"卡里没有 UV"对我们没有任何影响 —— 需要 UV 的场合只有"自己重新烘焙贴图"，
那属于 mod 制作流程，得去 mod 的 bundle 里拿。


---

## 57. 2.28：页签/表头改名 + 表头点击按数字排序

### 57.1 改名

| 位置 | 旧 | 新 |
|---|---|---|
| 第 2 页页签 | ② 按部位压缩（单张卡） | ② 单服装卡细分压缩 |
| 第 2 页大按钮 | ▶ 按部位压缩 | ▶ 单服装卡细分压缩 |
| 第 2/3 页表头 | 张 | 数量 |

改名要连带改：状态提示（「勾选要压的部位后点…」）、日志抬头（「…：N 个部位参与」）、
`chkAllSimilar` 的 ToolTip（原文里还写错了页号「③ 按部位压缩」，一并改成「▶ 单服装卡细分压缩」）。
命令行帮助文本（`parts` / `slots=`）也对齐成新名字。

⚠ **改名要顺带检查列宽**：表头 `张` 是 1 个汉字，`数量` 是 2 个 ——
`MinimumWidth` 还留在 30px 的话，表格被挤到最小宽度时表头会被裁成「数…」。
两页都提到 46px；「独占贴图」4 个汉字提到 78px。

### 57.2 表头点击排序

三个数字列（`cnt` / `size` / `own`）本来就能点，但 DataGridView 对**未绑定**行是按
显示字符串做**字典序**比较：

```
按字典序升序：1.80 MB, 18.16 MB, 2.22 MB, 20.97 MB, 8.92 MB   ← 明显是错的
按数字升序  ：1.80 MB,  2.22 MB, 8.92 MB, 18.16 MB, 20.97 MB
数量同理    ：11 张、13 张 会排到 3 张、4 张 前面
```

修法是挂 `SortCompare`，只对这三列接管比较；其余列原样返回，保持字符串排序能力：

```csharp
g.SortCompare += delegate (object s, DataGridViewSortCompareEventArgs e)
{
    if (!IsNumSortCol(e.Column.Name)) return;            // 其它列不管
    long a = SortNum(e.Column.Name, e.CellValue1), b = SortNum(e.Column.Name, e.CellValue2);
    e.SortResult = a.CompareTo(b);
    if (e.SortResult == 0) e.SortResult = string.Compare(...);   // 同值用文本兜底，顺序才稳定
    e.Handled = true;
};
```

`SortNum` 把显示串还原成数字：

| 列 | 显示 | 取什么 |
|---|---|---|
| `cnt` | `13` / `—` | 张数 |
| `size` | `14.58 MB` / `—` | 字节（按单位换算 B/KB/MB/GB） |
| `own` | `4 张 / 45.09 MB` / `—` | 斜杠**后面**那段的字节（"按大小排"要的是这个，不是张数） |

「—」= -1，所以没贴图的部位永远聚在一头。

### 57.3 两个必须一起处理的细节

1. **排序要能在重填表后活下来**。勾选框/下拉框一改、或者换卡重读，都会 `Rows.Clear()` 重建整张表，
   重建后 DataGridView 不会自己再排一次。所以把 `SortedColumn` + `SortOrder` 记在
   `sortP2Col/sortP2Dir`（第 3 页是 `sortP3Col/sortP3Dir`），填完表用
   `ReapplySort(g, col, dir)` 回放一遍。
2. **排完序要自己刷明细**。排序会重排行，但「当前行」跟着**行对象**走、行号常常没变 →
   `SelectionChanged` 不一定触发 → 右边「选中部位的贴图明细」会停留在旧部位。
   所以 `Sorted` 事件里再调一次 `FillTexList()` / `G3FillTex()`；
   重填表那一路用 `partsFilling` / `g3Filling` 挡住，避免刷两遍。

### 57.4 验证

| 检查 | 结果 |
|---|---|
| 数量升序（CardA.png，13 部位） | 3、4、4、4、5、8、11、13（字典序会给出 11、13、3、4…）|
| 独占降序（同上） | 45.09 MB、36.40 MB、6.01 MB、2.42 MB、1.14 MB、0 B，`—` 在末尾 |
| 大小升序（CardA.png） | 1.80、2.22、8.92、18.16、20.97 MB（8.92 在 18.16 之前 = 数字排序）|
| 大小降序（人物卡 DX4，55 行） | 49.59 MB … 70.90 KB |
| 重填表后排序是否保持 | 保持（SORT2P / SORT3P 两次输出一致）|
| 三页「文字截断」 | 0 |
| 真实窗口截图 | 两页表头「数量」完整；排序顺序与预期一致 |
| 默认路径语料回归 | 14/14 逐字节相同 |


---

## 58. 2.29：排序规则（部位固定顺序 / 数字列第一次点从大到小 / 无贴图永远在后）

### 58.1 规则

| 点哪一列 | 行为 |
|---|---|
| 部位（第 3 页是「组 · 部位」） | 只按**固定顺序**排一次，再点不反向；无贴图的部位**留在原位** |
| 数量 / 大小 / 独占贴图 | 第一次点**从大到小**，同列再点反向；无贴图的部位**永远在最后**（两个方向都是） |
| 其它列（材质等） | 保持原来的字符串排序 |

固定顺序 = 衣服槽位号本身：0 上衣、1 下衣、2 胸罩、3 内裤、4 手套、5 连裤袜、6 袜子、
7 鞋(内层)、8 鞋(外層)，饰品 1000+n；人物卡键是 `(组+1)*100000 + 部位键`，
所以先按组、组内再按部位号（`PartOrderOf` / `SlotOrder`）。

### 58.2 实现要点

```csharp
// 数字列第一次点必须"从大到小"，而 DataGridView 默认先升序 → 这四列改成 Programmatic，
// 点击由 ColumnHeaderMouseClick 自己接管；表头箭头也自己画
if (colName == "name") { st.Dir = ListSortDirection.Ascending; }        // 固定顺序：不反向
else if (IsNumSortCol(colName))
    st.Dir = (st.Col == colName && st.Dir == Descending) ? Ascending : Descending;
```

「无贴图永远在最后」这条要**按方向补偿**：`SortCompare` 返回的是升序语义的比较结果，
网格在降序时会自己取反，所以这一条写成
`int after = no1 ? 1 : -1; r = sortCurDesc ? -after : after;`
（数字本身仍按升序语义返回，交给网格取反）。同值时用部位序兜底，顺序才稳定。

排序状态（列 + 方向）记在 `GridSort` 里，重填表时用 `ApplySort` 回放
（勾选框、换卡重读都会重建整张表）；`Sorted` 事件里再刷一次明细，
因为重排行后行号常常没变，`SelectionChanged` 不一定触发。

### 58.3 验证

| 检查 | 结果 |
|---|---|
| 点「部位」（13 个部位） | 上衣、下衣、胸罩、内裤、手套、连裤袜、袜子、鞋内、鞋外、饰品槽 0… ；无贴图的留在原位 |
| 数量第一次点 / 第二次点 | 13、11、8、5、4、4…（末尾三个「—」）/ 3、4、4、4…（**末尾仍是三个「—」**）|
| 大小第一次点（人物卡 55 行） | 49.59 MB 起，从大到小 |
| 组·部位（人物卡） | 角色本体 → 换装1…换装7，组内按部位号 |
| 重填表后 | 排序保持 |
| 默认路径语料回归 | 14/14 逐字节相同 |


---

## 59. 事故与抢救：MainForm.cs 被 PowerShell 编码事故毁掉（含之后的三道门）

### 59.1 事故本身（是我操作错的）

2.29 收尾时要用批替换改 4 处 `DumpSortState(...)` 调用点，我图快用了 PowerShell 管道：

```powershell
(Get-Content -Raw $f) -replace '旧','新' | Set-Content -Encoding UTF8 $f
```

这台机器上 `pwsh` 实际是 **Windows PowerShell 5.1**。5.1 的 `Get-Content` 不带 `-Encoding` 时按
**系统 ANSI（cp936）**解码；源码是 **UTF-8 无 BOM**，于是整份文件被当成 GBK 读成乱码，
`Set-Content -Encoding UTF8` 又加 BOM 写回 —— 文件变成 `utf8(cp936_decode(原UTF-8))`。

更糟的是丢字节：cp936 解码遇到**非法字节对**（第二字节 < 0x40 或 = 0x7F）时，吐一个 `?`
**并吃掉这两个字节**。实测（受控实验，见 59.2）：

| 原文 | 字节流 | 结果 |
|---|---|---|
| `表头排序用）` + CRLF | `…EF BC 89 0D 0A` | `…EF BC 3F 0A` → 丢 `89 0D` |
| `"原尺寸", "4096"` | `…E5 AF B8 22 2C…` | `…E5 AF 3F 2C…` → 丢 `B8 22` |
| `放宽到 4096` | `…E5 88 B0 20 34…` | `…E5 88 3F 34…` → 丢 `B0 20`（那个空格）|
| `保护alpha`（无空格） | `…E6 8A A4 61…` | **无损**（`A4 61` 在 cp936 里是合法对）|

结论：**每个损坏点丢的是「汉字第 3 字节 + 紧跟的那个字节」，前两个字节留下**，所以每点有
64 个候选字符（`E4..EF` × 6 bit），且必须连"被吃的那个字节"一起补回。
全文件共 **1157 个损坏点**（另有 1 个裸 CR、4 个 NUL、106 行 CR 被吃掉、83 处多余引号）。

### 59.2 抢救过程（可复用的手法）

1. **反解编码往返**（`fix_mojibake.py`）：逐字符查 cp936 逆表，把乱码还原回原始 UTF-8 字节流。
   反解表按「单字节 + 双字节全表」建，PUA 部分用 gb18030 兜住。
2. **字符串字面量用编译产物当真值**：事故前 24 秒的那次构建留下了 `dist_t\KoiCardTexTool.dll`，
   里面 **#US 堆**存着每一条字符串字面量。按元数据头（`BSJB` → 流表 → `#US`）老实解析，
   拿到 1360 条**边界准确**的真值；再用「最长公共前缀」把源码里的字面量重写成真值。
   ⚠ 早先"扫 UTF-16 连续可打印串"的做法会因对齐漂移漏串、或把相邻串粘起来 → 匹配到错误的"真值"，
   反而把源码改坏（我踩过，改坏了 100 多处引号）。
3. **注释的文本靠语料**：会话日志（`synapse/workspaces.json` 里存着每次 edit 的 old/new 字面量、
   以及历次 read 的原文）、`_work\_backup_step3\MainForm.cs`（未受损旧版）、`使用说明.txt`、
   GUIDE 自身 —— 共 17 万行语料做锚定匹配，能精确恢复的就精确恢复。
4. **剩下的人工判定**：4 个并行子代理各领 120 余条，逐条给证据（block 约束 + 语料命中 + 字节级复现），
   我再用脚本机械校验（首字符必须在候选块内、第二字符必须是 < 0x40 的 ASCII）。
5. **编译器当裁判**：从 1664 个报错一路收敛到 0 个。
6. **回归当终审**：`run_v220.ps1` → `SAME=14 DIFF=0`（14 张卡产物与 2.19 基准逐字节相同），
   这是"救回来的代码行为和原版一致"的唯一硬证据。

### 59.3 之后的三道门（每次都跑）

```bash
# ① 编译
cd kkcard_exe && dotnet build -c Release -o dist_t --no-restore
# ② 回归（最硬的一把尺子）
powershell -NoProfile -File '_work\tex\run_v220.ps1'   # 期望 SAME=14 DIFF=0
# ③ 文件体检
kkcard_exe/check_encoding.sh
```

`check_encoding.sh` 查四件事：非法 UTF-8、开头 BOM、换行符混用、字符串未闭合（含 verbatim 串
跨行的正确处理）。另外顺手报 NUL 字节与"裸回车"。造了 4 个坏文件做反向验证：**4/4 全部抓到**。

### 59.4 禁令与新环境事实

- **禁止用文本管道批量替换源码**（`-replace`、`sed -i` 之类）。要批改就用字节级工具
  （`perl -i -pe` 或 python 显式按字节读写），并且改前改后对哈希。
- git bash 实测的三个坑：`sed -i` **会把 CRLF 改成 LF**（40→38 字节，CR 2→0）；
  `命令 文件 > 同一个文件` 先把文件清成 **0 字节**；`LANG` 为空时 `sed` 按字节匹配，
  能把一个汉字**切成半个**（造出和本事故同类的非法 UTF-8）。保真的做法是 `perl -i -pe` / python。
- 这台机器**没装 pwsh 7**，`pwsh` 落到 Windows PowerShell 5.1；要用就显式
  `powershell -NoProfile ...`，**不要用它读写源码**。
- 仓库已建：`_work\.git`（`core.autocrlf=false` + `.gitattributes` 里 `* -text`
  → git 不许自己改换行符），只跟踪**源码 + 脚本 + 文档**（356 个文件），13GB 测试素材不进仓库。
  出事后 `git checkout -- <文件>` 一条命令退回。开发流程写在 `kkcard_exe\README-dev.md`。


---

## 60. 2.30：明细表按大小排 / 一键统一两种"就近应用" / 组过滤支持超过 7 套

### 60.1 贴图明细表：列头「字节」→「大小」+ 数值排序

明细表（第 2 页 gridTex、第 3 页 gridGT）以前**没有接排序钩子**，点表头走 DataGridView 的默认
字符串比较，于是 "30.07 KB" 与 "2.64 MB" 之间、以及 "1024px"/"512px" 之间全是字典序 —— 现象就是
"点了没按大小排"。修法是把这两张表也挂上 `HookHeaderSort`（22.29 给部位表做的那套）：

```csharp
readonly GridSort stTex2 = new GridSort(), stTex3 = new GridSort();
HookHeaderSort(gridTex, stTex2, null);      // 明细表排序不需要联动别的表 → afterSort = null
HookHeaderSort(gridGT,  stTex3, null);
```

`IsNumSortCol` 扩到 `tid / tdim / tbytes`；`SortNum` 加两条：`tdim` 去掉 "px" 取数字，`tid` 按整数。
"第一次点的方向"按列分开：数字量（大小/尺寸）从大到小，编号（TexID）从小到大 —— `FirstClickDesc(col)`。

重填表后回放：`FillTexListCore` 末尾 `ApplySort(gridTex, stTex2)`、`G3FillTexCore` 末尾同理；
并且给两张明细表的 `SelectionChanged` 加了 `if (texFilling) return;` / `if (g3Filling) return;`
防"排序移动当前行 → 触发 SelectionChanged → 又重填表"的递归。

### 60.2 一键统一：两种就近应用

- 范围下拉新增「**仅当前选中项目**」：只把值写成**明细表当前选中那一张**的单张指定
  （`texOv3[id] = Rule(...)`），部位规则不动。
- 新按钮「**设置到目前浏览的部位**」：把值设到 `gridG.CurrentRow` 那一行（一个组·部位），
  只改这一行；并清掉该部位贴图的单张指定 —— 否则单张指定优先级更高，用户会以为"没生效"。

### 60.3 组过滤：写死 8 个 → 按实际套数动态生成

老实现 `readonly CheckBox[] chkGrp = new CheckBox[8]`，注释里也写着"位 g = 第 g 组：0=角色本体，1..7=换装"。
压缩器那边是：

```csharp
if (g < 0 || g > 30 || (charaGroupMask & (1 << g)) == 0) off.Add(KName(sk));   // 该组不参与
```

所以**只要卡里有第 8 套及以上换装，那些组的贴图永远进不了掩码 → 被静默跳过**（不压、不提示）。
改成：

- `List<CheckBox>` + 每项 `Tag = 组号`；`CharaMask()` 用 Tag 算位掩码（≤30）；
- `BuildGrpFilter(maxGroup)` 重建那一排（放在专门的子面板 `pGrpChk` 里，重建不动旁边的按钮），
  勾选状态按组号保留（`bool[] grpOn`，新出现的组默认勾上）；
- 扫描线程里顺带探测这批卡的最大组号（最多探 6 张，走 `ScanPartsCached`），`Post("groupmax", n)`；
- 组号 > 30 时在日志里明说一句"压缩器是 32 位掩码，这些组按不参与处理"，不再静默。

### 60.4 顺带修掉的界面 bug：第 3 页读完卡明细表是空的

`G3FillCore()` 末尾会调 `G3FillTex()`，但 `G3Fill()` 把它包在 `g3Filling = true` 里，而
`G3FillTex()` 开头就是 `if (g3Filling) return;` —— 于是这次填充被自己挡掉了：
**读完卡后「3) 选中组·部位的贴图」是空的，必须手点一行「组·部位」才会填**（2.25 引入防抖开关时留下的）。
修法是 `G3Fill()` 在 `finally` 之后（`g3Filling` 已复位）再补一次 `G3FillTex()`。

### 60.5 自检看门狗（因为踩到了）

自检里只要有一步弹出确认框，无头跑就会**卡死**，窗口还会留在用户桌面上等人点（2.30 自检真发生了一次）。
现在 `uitest` 路径会起一个 300ms 的定时器，扫到本进程的 `#32770` 模态框就 `WM_CLOSE`：

```csharp
var dlgWatchdog = StartDialogWatchdog();   // 300ms 一次，CloseDialogsNow() 关掉所有模态框
```

### 60.6 验证

| 检查 | 结果 |
|---|---|
| 点「大小」（第 2 页明细） | 35.59 → 14.50 → 1.35 MB（第一次从大到小）；再点反向 293.92 KB → 1.05 → 1.07 MB（按数值）|
| 点「尺寸」 | 4096px 两张在前，随后 1024px |
| 点「TexID」 | 1、5、33、41…升序 |
| 一键统一「仅当前选中项目」 | 只有 TexID 47 变 `png/2048[sh=0,shm=cas,ga=1]`，其余仍是"跟随部位" |
| 一键统一「仅当前选中组」 | 角色本体整组 → JPEG/512（老行为不变）|
| 组过滤按 12 套重建 | 13 个复选框；勾 本体/换3/换12 → 掩码 4105（第 0、3、12 位）|
| 第 3 页读完卡 | 明细表自动填好（以前是空的）|
| 三页「文字截断」 | 0（含 12 套的长行）|
| 默认路径语料回归 | 14/14 逐字节相同 |


---

## 61. 2.31：「仅当前选中项目」→「仅已启用的部位」

2.30 加的那一项本意是"就近应用"，但语义选错了：它改的是**明细表里当前选中那一张**（单张指定），
而实际使用中想要的往往是"**只处理我勾上的那些部位**"（先把不压的取消勾选，再统一）。

改成：

```csharp
const string G3ScopeEnabled = "仅已启用的部位";
// G3UniApply 里：
if (scope == G3ScopeEnabled)
{
    foreach (DataGridViewRow row in gridG.Rows) {
        if (row.Tag == null || !Chara.IsCharaKey((int)row.Tag)) continue;
        bool on = row.Cells["on"].Value is bool && (bool)row.Cells["on"].Value;
        if (!on) continue;                    // ← 没勾的一律不动
        ...写入 fmt/max/sh/shm/ga，并收集该行贴图的 id...
    }
    foreach (var id in idsOn) texOv3.Remove(id);   // 只清"已启用"那些部位的单张指定
}
```

与其它范围的区别：

| 范围 | 命中 |
|---|---|
| 全部组 | 卡里所有「组·部位」行（不管勾没勾）|
| 换装N / 角色本体 | 该组的全部行 |
| 仅当前选中组 | 当前选中行所属组的全部行 |
| **仅已启用的部位** | 只命中「启用」勾上的行 |

`G3UniToSelectedTex`（旧的"设到选中那一张"）已删除 —— 单张贴图仍然可以直接在明细表里改
那一行自己的组合框（单张指定优先级最高），不需要再占一个范围项。

验证：DX4（55 行）取消第 2、3 行启用 → 「仅已启用的部位」报「已启用的 53 个」；「全部组」报 55 个。


---

## 62. 2.32：彩色贴图降采样核改默认（+ 两个"老预设被悄悄改掉默认值"的 bug）

### 62.1 起因与结论

用户提的是"给角色卡贴图做超采样"。先把"超采样"拆成三件不同的事：

| 读法 | 内容 | 结论 |
|---|---|---|
| A 真·超采样（把降采样做对） | 降采样核必须是"输出像素覆盖的那块源像素的加权平均" | **本轮做掉** |
| B 放大（×2/×4） | 让贴图本身变大 | 留作下一步（体积反向增长） |
| C AI 超分 | ESRGAN 类，能"造"细节 | 不做（体积 +25~40MB、CPU 分钟级、对图案有幻觉） |

A 的具体缺口：`Resample.Applies(cls)` 让数据贴图（法线/掩罩/高度）走面积平均，
彩色贴图**默认**走 GDI+ `HighQualityBicubic`（开关叫 `colorarea`，2.31 及以前默认关），
而双三次的负瓣会压掉高频、并在边缘留下环 —— 后面接着的 CAS 锐化还会把环一起放大。

### 62.2 单变量 A/B（全语料）

同一批 14 张卡、`1024 auto 90 1024 plan=p_zl.json own=1 ownmax=4096 jobs=4`，
唯一变量就是 `colorarea`。三条臂：

| 臂 | 核 | premul | 整卡合计 |
|---|---|---|---|
| ca0 | GDI+ 双三次 | 关 | 356,122,243 |
| ca1p0 | 面积平均 | 关 | 355,470,939（−0.18%） |
| ca1 | 面积平均 | 开 | 355,069,733（−0.30%） |

ca0 与文档里的 2.19 基线**逐字节相等**，确认对照臂就是当时的行为。

质量侧（口径与 `h_kernel_vs_sharpen.py` 一致：产物双线性放大回原尺寸，中心 2048²，win=8）：

| 对比 | 变化贴图数 | SSIM | 边缘保持 | 过冲(振铃) | 这 15 张字节 |
|---|---|---|---|---|---|
| ca0 → ca1（**实际发布**） | 15 | 15/15 上升，均值 +0.0033 | −1.0pp | −0.90pp | −4.1% |
| ca0 → ca1p0（只换核） | 15 | 均值 −0.0012（11 涨 4 跌，平手） | +1.3pp | −0.56pp | −2.5% |

**归因**：只换核对 SSIM 是平手（更锐但更软），主要是**预乘 alpha 平均**把 SSIM 拉了回来
（它只对真有 alpha 的贴图起作用，正好是"透明区垃圾色混进边缘"那批）。
也就是说这条路**同时**打开了 premul —— GDI+ 那条路根本不用 premul。

**改动范围自证**：内容变化的 15 张**全是彩色类**（maintex 12 / reflection 2 / matcap 1），
其余 196 张**逐字节不变**（含全部法线/掩罩/高度）。加上"默认臂与 ca1 逐字节相同、
`colorarea=0` 与 ca0 逐字节相同"两条，等于把"只有该变的变了"钉死。

### 62.3 顺手修掉的两个真 bug（都是"老预设被悄悄改掉默认值"）

改默认值会暴露"字段缺省"的语义问题。`colorArea` 是预设字段，老预设里**没有**它：

1. **命令行**：`PresetIo.TryReadPlanEx` 的返回值是"预设里有没有类型表"，
   而 `Program.cs` 把它当成了"有没有 colorArea 字段"用（变量名还叫 `caSeen`）。
   于是"有类型表但没写 colorArea"的老预设被当成 `false` 照收，把 2.32 的新默认关掉。
   实测抓到：老预设跑出来逐字节等于旧核。改法：加一个真报"字段在不在"的重载
   （和 `sharpenModeExplicit` 同一套思路）。
2. **界面**：`PreApply` 里无条件 `ApplyProcessFlags(ca, sh)`，同样把缺省当成 false。
   改成 `caSeen ? ca : Resample.ColorMode`。

修完的三种情形都有实测（命令行 `preset=` 与界面 `presel=` 各测一遍，都是逐字节比对的产物）：
显式 true → 面积平均；显式 false → GDI+ 双三次；**没有字段 → 跟随新默认**。

### 62.4 教训

- **注释里的数字会过期，而且会误导人**。`Resample.ColorMode` 原来写着"代价 +17~36% 字节"，
  那是早期**没有锐化、单张贴图**条件下量的最坏情况。我据此在方案里写了"体积零变化"，
  又说成"bicubic 只取 4×4 邻域"—— 两条都不准。实测下来是：整卡 −0.30%，
  唯一 +31.2% 的那张反而是"细节更多"。**先测再写**。
- **测试钩子本身会污染观测**。诊断"预设选中后开关没变"时，三次"被改回 true"的记录
  追到最后是我自己的 `UiColorKernel(colorkernel)` 探针在晚一点的时候跑了一遍。
  加调用栈（`StackTrace` 取第 3~6 帧）当场定位，比继续猜快得多。
- **`Substring` 的数字要数**：`"colorkernel="` 是 12 个字符不是 11，
  数错一位就变成"永远当成开"，而且不报错。
- **门禁要能被"预期内的变化"表达**。默认值一改，"与 2.19 逐字节相同"必然失效；
  与其放宽成"允许一些差异"，不如把**确切的差异集合**（排序后名单的 MD5 + 条数）钉进脚本：
  多一张、少一张都还是红。
- **同一个开关管两件事，早晚会拧巴**（§41.7 已经写过一次）：这次又出现"预设里的字段缺省值"
  被当成"用户明确选择"。凡是"能关掉某个改进"的开关，都要能区分"没写"和"写成 false"。



---

## 63. 2.33：放大（×2/×4）——以及被它钓出来的一个静默吞掉改动的兜底

### 63.1 放大的口径

「超采样」的三种读法（§62.1）里，这一轮做的是 **B：放大**。要点：

- **只对彩色类**：法线/掩罩/高度忽略。理由不是"不好看"而是"没意义且很贵"——
  采样方式决定更大尺寸不带来新信息，插值反而会动语义（法线要保单位长度与方向、掩罩是权重）。
- **双重上限**：「最大边」与 `Resample.UpCeiling`（默认 4096）取小，装不下就降级 ×4→×2→0。
  「最大边」= 目标上限，既管缩小也管放大，语义统一。
- **默认关**：这条路让体积涨 30~140%，必须用户明确开。

### 63.2 选核（往返实验，8 张真实彩色贴图）

放大没有真值可比，所以用往返：原图 → 面积平均 ÷2 → 候选核 ×2 → 与原图**同尺寸**比。

| 核 | SSIM | PSNR | 锐度 | 振铃 | ×2 字节 vs 不放大 |
|---|---|---|---|---|---|
| lanczos3（默认） | 0.9762 | 41.54 | 92.1% | 0.36% | 2.88x |
| lanczos2 | 0.9757 | 41.15 | 88.6% | 0.25% | 2.82x |
| catmull | 0.9758 | 41.09 | 88.2% | 0.19% | 2.80x |
| mitchell | 0.9731 | 40.30 | 84.0% | 0.00% | 2.72x |
| bilinear | 0.9715 | 39.82 | 82.0% | 0.00% | 2.68x |
| nearest | 0.9713 | 38.65 | 91.1% | 0.00% | 2.95x |

结论：lanczos3 保真与锐度都最好，振铃仅 0.36%，字节只贵 7% → 默认。
**真实代价**（全语料、1024/auto/90）：全部彩色类 ×2 → **+36.0%**（单卡 0~+142.2%）；
只放大主贴图 → +33.9%；极端单张（2048 带 alpha 主贴图，只能存 PNG）4.81 MB → **29.25 MB**。

### 63.3 三个必须写下来的坑

**① 放大不能用降采样的核公式。** 项目里 `hk.resize_axis` 是给降采样写的：
`w = K((idx-c)/s)`，s = 源/目标，缩小（s>1）时按比例**展宽**核 —— 正确。
照抄到放大（s<1）会把核**收窄**：×2 时算出 `[1.125, -0.125]` 这种 2 抽头"锐化"权重，
插值退化成最近邻；而且 lanczos2 与 catmull 的归一化权重在这个网格上巧合相等
（0.57316/0.50948 ≈ 0.5625/0.50 = 1.125），于是两者给出**逐字节相同**的图。
第一版离线实验整个是错的，是"两种核结果完全一样"这个不可能现象把它暴露出来的。
正解：`f = max(1, s)`，放大不低通、用核的天然宽度。

**② 振铃的参考区间要跟着核的支撑走。** 用"覆盖到的那一个源像素"当参考（降采样的口径）
会让双线性也报 8% 的假振铃；正确区间是"核支撑范围内的源像素 min/max"。改完：
双线性/无核三次 0.00%，lanczos2 0.25%，lanczos3 0.36% —— 只有负瓣核才振铃，符合理论。

**③ 压缩工具里那条"结果不比原图小就退回原字节"的兜底，会把放大整个吃掉。**

```csharp
if (use.Length >= raw.Length) { use = raw; fmtName = "orig"; j.NSame++; }   // 2.32 及以前
```

这条对缩小/重编码是正确的（压缩不该让贴图变大），但**放大后必然比原图大** ——
于是放大结果被原地丢掉，装进卡里的还是原始尺寸。更糟的是日志里
`st.After = tw + "x" + th` 记的是**目标**尺寸，所以日志写着 `2048x2048`，
产物里其实是 `1024x1024`，看起来像"放大了"。

发现过程值得记：我先是看日志，觉得没问题；是**逐 TexID 审计产物**（比尺寸/字节/格式）
才发现"同尺寸却涨了 3~6 倍字节"。修法两处：
- 放大过的贴图（`upApplied`）不走那条兜底；
- `st.After` 改成报**实际写出去**的尺寸（退回原字节时就是源尺寸）。

教训：**日志是程序的自述，产物才是事实**。凡是"改动是否真的生效"这类问题，
一定要去读产物（尺寸/字节/像素），不能只读日志。

### 63.4 验证

| 项 | 结果 |
|---|---|
| 默认关 vs 2.32 | 14/14 **逐字节相同** |
| 引擎正确性（C# vs Python 参考实现） | 3 张贴图最大差 **0~1 LSB**，平均差 0.0000 |
| 只改目标类（产物审计） | 数据贴图被改 0 张；彩色彩类按规则变 |
| 预设往返 | `maintex` 有 `up`，其余 8 类字段形状与 2.32 一致（不开时不写字段） |
| 端到端（preset 驱动） | 6 张 maintex 变大，材质球/反射/法线/掩罩一行没动 |
| 门禁 | `SAME=5 DIFF=9`，差异名单哈希与 2.32 相同（默认路径未变） |



### 63.5 「缩小后再放大」能不能省体积？（能不能，以及为什么不划算）

这是"能不能拿放大当压缩手段"的问题，直接用真实 PNG 贴图实测（`downup_study.py`）。
三条臂 + 一条对照，**同一个编码器**（PNG optimize / JPEG q90 取小）：

  O 原图 ｜ B 只缩小 ÷2（面积平均） ｜ C 缩小 ÷2 再放大 ×2（lanczos3，回到原尺寸）
  D 直接对**原图**用 JPEG，质量压到与 C 差不多贵为止

| 卡片·TexID | 尺寸 | 原图 | 只缩÷2 | 缩+放×2 | C/O | C/B | C 的画质 | 同体积的 D 画质 |
|---|---|---|---|---|---|---|---|---|
| 薄纱蕾丝裙黑白 · 3 | 512 | 42,587 | 15,086 | 37,919 | 89% | 2.51x | 0.9753 | **0.9911** (q87) |
| Alice Rizela · 1 | 4096 | 2,673,768 | 843,665 | 2,111,700 | 79% | 2.50x | 0.9504 | **0.9833** (q84) |
| Cartethyia B · 1 | 8192 | 2,218,612 | 611,781 | 2,162,560 | 97% | 3.53x | 1.0000 | 1.0000 (q87) |
| Cartethyia W · 1 | 512 | 23,504 | 7,973 | 21,088 | 90% | 2.64x | 0.9908 | **0.9919** (q87) |
| KK20240212 · 2 | 4096 | 926,853 | 297,141 | 977,539 | **105%** | 3.29x | 0.9948 | **0.9966** (q90) |
| KK20250109 · 1 | 4096 | 638,868 | 181,948 | 597,082 | 93% | 3.28x | 0.9942 | **0.9988** (q84) |
| KK20250126 · 1 | 4096 | 4,926,086 | 958,424 | 3,042,842 | 62% | 3.17x | **0.4865** | **0.9502** (q74) |
| KK20250417 · 2 | 1024 | 5,695 | 1,816 | 5,695 | 100% | 3.14x | 1.0000 | （没有同等体积的 JPEG） |
| KK20250607 · 1 | 2048 | 331,314 | 91,367 | 295,111 | 89% | 3.23x | 0.9931 | **0.9938** (q87) |
| KK20250806 · 3 | 4096 | 5,204,130 | 903,130 | 2,951,544 | 57% | 3.27x | **0.7252** | **0.9140** (q78) |
| **合计** | | **16,991,417** | **3,912,331 (23.0%)** | **12,203,080 (71.8%)** | | **3.12x** | | |

三条结论：

1. **跟原图比：多半会省，但不是稳赚。** 10 张里 8 张省了 3%~43%，可也有两张**反而更大**
   （105%、100%）—— 那些是本来就平滑/量化的贴图，没有高频可丢。而且**省得最多的那两张画质崩了**
   （SSIM 0.4865 / 0.7252）—— 省得越多、崩得越狠，因为是同一个机制（丢高频）的两面。
2. **跟"只缩小"比：纯亏 3.12 倍。** 放大版的内容本来就是从缩小版插值出来的，
   它不可能比缩小版多任何信息，画质上限就是缩小版；体积却要 3 倍。
   在"体积 × 画质"这张图上，**缩小再放大被"只缩小"严格支配**（同样的画质，3 倍体积）。
3. **同样体积下有更好的做法：直接对原图降质量（JPEG）。** 9 张可比的里，
   D 的画质**全部 ≥ C**，其中两张是碾压（0.9502 vs 0.4865、0.9140 vs 0.7252）。

机制上的原因：JPEG 是**自适应**地丢高频（平坦区多丢、边缘少丢），而"缩+放"是
**无差别**地把 2×2 以上的细节一律抹平。所以在同样的字节预算下，自适应的那个总是赢。

**实践建议**：想省体积就直接把「最大边」调小（尺寸变小才是省体积的正道）；
尺寸不能变又要省体积就换 JPEG / 降 JPEG 质量。
只有当下游**硬性要求尺寸不变、且不能用 JPEG**（必须是 PNG、或有 alpha 不能接受无 alpha）时，
"缩+放"才是个可用的下策 —— 但要清楚它比"干脆缩小"贵 3 倍。


---

## 64. 2.34：放大选项默认收起 + 补齐第 2/3 页

### 64.1 为什么收起来

2.33 把放大摆在界面上，但它是"多数人用不上、代价又大"的功能（全彩色类 ×2 → 整卡 +36%，
极端单张 4.81 MB → 29.25 MB）。默认展开等于让一个高代价开关去占"常用设置"的位置。
所以：**默认全部隐藏**，三个页面各有一个「显示放大选项」按钮，按下才展开。

- 第 1 页：按钮放在类型表**表头最右边** —— 紧挨着被它展开的那一列，找得到。
  隐藏的手段是「列宽设 0 + 控件 Visible=false」；表头那一格换成 FlowLayoutPanel 装「扫描结果 + 按钮」。
- 第 2/3 页：按钮放在「一键统一」那一行的行首；两张表格的「放大」列用 `DataGridViewColumn.Visible` 隐藏。
- 展开状态**三页联动**（和「贴图细节保护」那几个开关一路：改一处三页同步），但**不进预设**
  —— 预设里只存倍率本身。

### 64.2 「跟随」是默认值，这一点是刻意的

第 2/3 页表格里的「放大」列默认 **「跟随」**（不表态），而不是「不放大」。

理由：部位规则是**覆盖**类级的。第 1 页设了主贴图 ×2，如果第 2/3 页的列默认写「不放大」，
那每一行部位规则都会把类级设置顶掉，用户在批量页设的放大到了单卡页就"凭空消失"。
这类"某处悄悄覆盖另一处"的问题本项目已经踩过好几次（§62.3 的两个 bug 同源），
所以新字段一律：**没表态 = 跟随，不覆盖**。

### 64.3 验证

| 项 | 结果 |
|---|---|
| 默认收起 | `展开=False 第1页 0/12 可见 … 表格列 0/4 列宽=0` |
| 按一下展开 | `展开=True 第1页 12/12 … 表格列 4/4 列宽=92` |
| 三页截图（收起 / 展开） | 第 1/2/3 页两种状态都对，`CLIP` 三页均"没有文字被截断" |
| 第 2 页一键统一带放大 | 部位表 dump 出现 `|×2`；贴图规则 dump 出现 `up=2` |
| 第 3 页一键统一带放大 | 日志 `[统一] 全部组 → … / 灰度2通道 开 / 放大 ×2（157 个条目…）` |
| 默认路径产物 | 回归门禁 `SAME=5 DIFF=9`，差异名单哈希与 2.33 相同（界面改动不影响产物） |
| 编码体检 / 占位符审计 | 14 文件干净 / 0 处不一致 |

### 64.4 顺手修的两个格式化 bug（含一个潜在崩溃）

给 dump 加字段时发现 `StringBuilder.AppendFormat` 的**占位符数量与实际参数对不上**：

1. `UiPartsGridDump`（部位表快照）：我加的第 11 个参数没有对应的 `{10}` → 放大列被静默丢掉。
2. `UiPartsTexDump`（明细表快照）：**历史 bug**，11 个占位符对 12 个参数 ——
   「最终处理」列从加上那天起就没被打印过一次。
3. 第 2 页布局 dump 里有个分支：3 个占位符只给 1 个参数 —— 真跑到会抛 `FormatException`。

`AppendFormat` 多给参数**不报错**，所以这三处都是"看着正常、其实少了东西"。
为此写了 `_work/tex/check_format_args.py`（数占位符 + 数参数 + 报不一致），
现在这两个文件 0 处不一致。**教训：dump 类工具的字段要能被断言，不能只看"没报错"。**



---

## 65. 2.35：放大选项的位置/文案调整（7 处）+ 一次自己造的 NRE

### 65.1 七处界面调整

| # | 要求 | 做法 |
|---|---|---|
| ① | 表格里「放大」列不要「跟随」 | 项改为 不放大 / ×2 / ×4；**不放大 = 这一级不表态** |
| ② | 第 3 页「独占贴图保护」与「上限」同行 | 两者包进一个 `WrapContents=false` 的子面板，成为一个不可拆单元 |
| ③ | 第 2/3 页展开按钮放到「独占贴图保护」后面，展开项紧跟按钮 | 调整 `Controls.Add` 顺序，按钮夹在独占单元与放大项之间 |
| ④ | 第 2 页两个「应用」按钮单独一行 | 给包装 `TableLayoutPanel` 加一行，按钮移进新行 |
| ⑤ | 第 3 页两个动作按钮单独一行 | 同上（`gWrap` 加一行） |
| ⑥ | 第 3 页「一键统一」按钮改名「应用到所有部位」 | 只改按钮文案，面板标题不变 |
| ⑦ | 第 3 页「范围」去掉「仅当前选中组」 | 不再往 Items 里加；`G3UniApply` 里的兼容分支保留（老预设/命令行传这个词仍然照旧） |

**关于①的语义**（写下来免得以后忘）：表格里选「不放大」= 不表态，等价于"跟随类型表"。
这样部位行不会因为默认值把第 1 页设的类级放大顶掉 —— 那类"某处悄悄覆盖另一处"的问题
本项目已经踩过好几次（§62.3、§64.2）。代价是**没法在这一级显式关掉**：
若类型表设了主贴图 ×2，想只对某个部位关掉，只能改类型表或不用那一行。
（如果以后确实需要"显式关"，再加一个「关」项即可，不能靠「不放大」兼职。）

### 65.2 我把三处 null 保护删掉了，用户当场撞到崩溃

改 ① 的时候，我把这一行

```csharp
row.Cells["tup"].Value = (hasOv && ov.Up >= 0) ? UpValToItem(ov.Up) : "跟随部位";
```

改成了

```csharp
row.Cells["tup"].Value = UpValToItem(ov.Up);      // ← 丢掉了 hasOv 的短路！
```

原来那行的 `hasOv &&` 是**短路保护**：这张贴图没有单张指定时 `ov` 就是 `null`，
`ov.Up` 根本不会被求值。改成无脑求值后，**每一张没有单张指定的贴图都会 NRE**。

用户报回来的栈很清楚：

```
System.NullReferenceException
  at MainForm.FillTexListCore()
  at MainForm.FillTexList()
  at <>c__DisplayClass289_0.<BuildPartsPage>b__15(...)   ← gridParts 的 CellValueChanged
  at DataGridViewCell.set_Value(Object value)
  at MainForm.FillPartsGridCore()
```

同类错误一共 **3 处**（我逐个查出来的）：

| 位置 | 空引用 | 修法 |
|---|---|---|
| `MainForm.FillTexListCore`（第 2 页明细） | `ov` 为 null | `(ov == null) ? "不放大" : UpValToItem(ov.Up)` |
| `MainFormChara.G3FillTexCore`（第 3 页明细） | `ov` 为 null | 同上 |
| `MainFormChara.G3FillCore`（第 3 页部位表） | `r` 为 null（`has == false`） | `(r == null) ? "不放大" : UpValToItem(r.Up)` |

**为什么我自己没先发现**：改完这一批之后我只跑了截图（`snap=`），截图发生在 tab1 段、
**在 FillTexListCore 之前**，所以进程"看起来正常"；而 `tab2=1` 那条自检我当时没重跑。
教训：**改了"每次重填都会走"的代码（表格填充），必须跑一遍所有页面的读卡路径**，
不能只看截图 —— 截图拍的是布局，不是数据填充。

为此加了 `_work/tex/ui_smoke.sh`：14 个场景（3 张衣服卡 × 隐藏/展开 + 一键统一两条 +
预设 + 人物卡隐藏/展开/统一/按钮 + 第 1 页两种），**每个场景都断言"退出码 0 且日志里没有
Exception/异常"**，不再只看有没有输出。

### 65.3 验证

| 项 | 结果 |
|---|---|
| 界面自检 15 场景 | 全部 rc=0、异常=0（`ui_smoke.sh`） |
| 展开状态 | 第 2 页 0/2 → 2/2、第 3 页 0/2 → 2/2、表格列 0/4 → 4/4、列宽 0 → 92 |
| up=2 仍然落地 | 第 2 页 `34=auto/1024[sh=100,cas,ga=1,up=2]`；部位表 `…|开|×2`；第 3 页 `[统一] … / 放大 ×2（157 个条目…）` |
| 范围下拉 | `全部组 / 角色本体 / 换装1..7 / 仅已启用的部位`（无「仅当前选中组」） |
| 产物 | 回归门禁 `SAME=5 DIFF=9`，差异名单哈希与 2.34 相同（纯界面改动） |
| 编码体检 / 占位符审计 | 14 文件干净 / 0 处不一致 |



---

## 66. 2.36：批量页放大选项的落位 + 「应用放大设置」

### 66.1 三处改动

1. **「显示放大选项」按钮从类型表表头搬到第 3 行**（「独占贴图保护」后面），
   表头那一格恢复成纯标题「扫描结果」。这样三个页面的按钮位置一致（都在独占保护后面）。
2. **展开出来的「放大 / 放大算法」紧跟按钮**：第 3 行变成
   `独占贴图保护 [上限▾] [显示放大选项▾] 放大[▾] 放大算法[▾] [应用放大设置]`。
   原来挂在「锐化」行右边的「放大算法」搬过来了（连带删掉不再需要的独立 Label —— 现在由 `Pair()` 自带标签）。
3. **新增「应用放大设置」按钮**：把全局「放大」下拉的倍率一次性写进类型表里
   **所有已勾选**的类，没勾的类（= 原样不动）跳过并计数，状态栏与日志报明细。

### 66.2 为什么需要「应用放大设置」

放大是**逐类**设置的（类型表里一列），而实际使用中「把主贴图放大到 ×2」这种意图是全局性的。
没有这个按钮时，要放大 4~5 个彩色类就得点 4~5 次下拉。有了它：选一次倍率 → 点一下 → 逐类微调。

和第 2/3 页的「应用到所有部位」是同一个思路（那两页有「一键统一/应用到所有部位」），
只是作用对象从"部位行"换成"类型表行"。

### 66.3 验证

| 项 | 结果 |
|---|---|
| `upall=×2`（走真实按钮） | `maintex=auto/0/up2 normal=auto/0/up2 … other=auto/0/up2`（9 个类） |
| `upall=不放大` | 一个类都不带 `up` 字段（类级放大全部撤掉） |
| 展开状态 | 第 1 页 `0/13 → 13/13`（多出来的 1 项就是「应用放大设置」按钮）、列宽 0 → 92 |
| 界面自检 | 17 场景全部 rc=0、异常=0（`ui_smoke.sh`） |
| 第 1 页截图 | 收起态只有按钮；展开态 5 项齐全且**没有换行到第二行**、三页均无截断 |
| 产物 | 回归门禁 `SAME=5 DIFF=9`，差异名单哈希与 2.35 相同（纯界面改动） |
| 编码 / 占位符审计 | 14 文件干净 / 0 处不一致 |



---

## 67. 2.37：表格列宽拖不动 —— `AutoSizeColumnsMode.Fill` 的语义陷阱

### 67.1 现象与根因

用户报："部位一览里拖动表头无法放宽，拖动最右边的表头（灰度2通道）无法往右扩大，其他所有框都存在这个问题。"

根因就一行：

```csharp
gridParts.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
```

`Fill` 的语义是"**按 FillWeight 把列宽摊平到正好填满可视宽度**"，也就是每次布局
都会重新算一遍所有列宽。后果有两条：
- 手动拖宽 → 下一次布局就按比例重算 → **当场还原**（看起来像"拖不动"）；
- 最后一列想变宽 = 总宽要超过可视宽度 → 被钳住，**完全拖不动**（正是用户描述的那一列）。

四个表格都是这么设的（`gridParts` / `gridTex` / `gridG` / `gridGT`），所以"所有框都有这个问题"。

### 67.2 修法

```csharp
static void MakeColumnsResizable(DataGridView g)
{
    g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;   // 先按权重铺一次
    LayoutEventHandler once = null;
    once = delegate {
        if (g.IsDisposed || g.ClientSize.Width <= 160) return;      // 等一次像样的布局
        g.Layout -= once;
        int avail = g.ClientSize.Width - (g.RowHeadersVisible ? g.RowHeadersWidth : 0);
        double sum = 0;
        foreach (DataGridViewColumn c in g.Columns) if (c.Visible) sum += c.FillWeight;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;   // 之后列宽由用户说了算
        foreach (DataGridViewColumn c in g.Columns)
            c.Width = Math.Max(c.MinimumWidth, (int)Math.Round(avail * c.FillWeight / sum));
    };
    g.Layout += once;
}
```

两个细节值得记：

- **不要"抓 Fill 当时的宽度"当初始值**。第一版我这么写，抓到的是布局早期控件还是全宽时的值，
  算出来总宽 1200，而 Fill 本来是 728 —— 初始观感直接变了。改成**按 FillWeight 和当前可视宽度直接算**，
  才是 Fill 原本的意图。
- `MinimumWidth` 之和本来就大于可视宽度时（这张表就是），结果自然溢出成横向滚动条，
  这与 Fill 模式下的表现一致，不用管。

### 67.3 受控对照实验（把"以前拖不动"实测出来）

临时把那一行改回 `Fill`、单独构建一个对照版本，跑同一个"最后一列往右拖 60px"的操作：

| 模式 | 结果 |
|---|---|
| **Fill（旧）** | `parts.ga: 62 → 62`（要求 +60，**实际 +0**） |
| **None（新）** | `parts.ga: 62 → 122 → 182`（要求 +60，**实际 +60**） |

四个表格、左右两端都验过（`ui_smoke.sh` 里 5 个 `dragcol=` 场景 + "实际 +0 即判失败"的断言）。

### 67.4 又一个"插入点插错"的教训（这次是自己踩的）

为了做上面这个自检，要把探针插到"tab2/tab3 段末尾"（那两段末尾就 `return 0` 了）。
我用"找 `HoldUi(f, hold);` 那一行、插到它前面"的办法，结果：

1. 先插了第一处 → **索引整体位移 10 行** → 第二处插到了 `if (sort3 != null)` 的
   **条件与函数体之间**。C# 允许这种写法（后面的 `{...}` 变成无条件块），于是 sort3 的代码
   在 `sort3 == null` 时也执行 → `NullReferenceException`。
2. 删的时候我又用了"删到下一个 `}`"的规则，留下了一个多余的 `}` → 花括号失衡，
   另一处 `try` 没了 `catch`。

两处都是**插入/删除的定位规则不够严**。改进做法（已照此重做）：
- 定位时同时校验**缩进**与**前后行**（`前置 == }` 且 `后置 == return 0;` 且缩进 == 24）；
- 多处插入**按倒序**执行，避免索引位移；
- 改完立刻 `git diff` 逐行核对（这次正是靠它确认"只多了该多的地方"，把误插的两块全找出来）。

顺便：`Error CS1524: 应输入 catch 或 finally` + `CS1519: 标记 catch 无效` 这一对错误，
就是"多了一个 `}` 把 try 提前闭合"的典型症状，以后见到可直接往这个方向查。



---

## 68. 2.38：列宽记忆、附带四个预设、按贴图类型统一

### 68.1 列宽记忆（`ui-layout.json`）

- 「记住列宽」把四个表格的列宽按**列名**写进 exe 旁边的 `ui-layout.json`；启动时自动套用。
- 「恢复默认列宽」删文件并回到按权重的默认比例。
- 只记 `ClientSize.Width > 160` 的表格 —— 没显示过的表格宽度还是占位值，记下来没意义。

**踩到的 bug（值得记）**：第一版我写成

```csharp
if (ApplySavedColWidths(g, key)) return;      // 有保存的就用保存的
... 计算默认 ...
g.AutoSizeColumnsMode = None;
```

——**提前 return 把"切成 None"跳过了**，于是有保存列宽时：模式还是 Fill，
保存的宽度下一次布局就被摊平覆盖，拖动又回到"拖不动"。两个症状叠在一起，很难认。
正解是**先切模式、再设宽度**：

```csharp
g.Layout -= once;
g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;   // 先切
if (!ApplySavedColWidths(g, key)) ApplyDefaultColWidths(g);     // 再设
```

顺序的原因很直接：**Fill 模式下设好的宽度留不住**，所以必须先离开 Fill。

验证（三次独立进程）：拖动 ga→182 →「记住列宽」→ 文件里 `parts.ga=182`
→ 新进程启动 `parts 总宽=790`（默认是 670）、`ga: 182 → 182` ✓ → 「恢复默认列宽」删文件并回到 670 ✓

### 68.2 附带四个预设

`EnsureDefaults()`（首次运行、预设目录为空时执行）从 3 个改成 4 个：

| 预设 | 主贴图 | 其余 | 锐化 | 独占保护 |
|---|---|---|---|---|
| 仅优化 | 原尺寸 | 原尺寸 | 100% | 原尺寸不降 |
| 质量 | 2048 | 1024 | 100% | 4096 |
| 性能 | 1024 | 512 | edge 150% | 2048 |
| 性能（激进） | 1024 | 512 | edge 200% | **关** |

后两个按用户实测后保存的那两套写（`auto` 格式；老的「性能」是 `png`/全 1024，已被取代）。
注意 `EnsureDefaults` 只在目录为空时跑，所以**已有安装不会被改动**，新包才带这四个。

### 68.3 按贴图类型统一（第 2/3 页的展开栏）

- 展开栏：「按贴图类型统一 ▾」按钮 + 一个 `WrapContents=false` 的子面板
  （「贴图类型 [下拉] [应用到该类型贴图]」）—— 用户明确要求"这两个不能分开"，
  所以包成一个不可拆单元（和「独占贴图保护 + 上限」同一手法）。
- 作用范围：**整张卡**里该类型的全部贴图（`ps.Parts` / `ps3.Parts` 全遍历，按 `PartTex.Cls` 过滤），
  写成单张指定（`texOv` / `texOv3`，按 TexID），优先级最高。
- 取值沿用「一键统一」那一套（与「应用到本部位全部贴图」同一约定：`跟随部位` 走 auto/1024 兜底）。
- 状态栏报命中数；命中 0 张也明说（那张卡里没有这一类贴图）。

验证：第 2 页主贴图 **21 张**、第 3 页主贴图 **141 张**，`texOv` 里逐 TexID 出现 `up=2` 等字段 ✓

### 68.4 本轮的自检扩充

`ui_smoke.sh` → 27 个场景，新增：按类型统一（主贴图/遮罩、第 2/3 页）、记住列宽、恢复默认列宽；
并加了两条"判失败"条件：**拖动被还原（实际 +0）** 与 **按类型统一命中 0 张**。



---

## 69. 2.39：按类型统一加「选中部位」、4096 档、默认预设改质量

### 69.1 三个改动

1. 「应用到该类型贴图」→ **「应用到所有该类型贴图」**（整张卡，文案写全）。
2. 新增 **「应用到选中部位」**：只改当前选中那个部位 / 「组·部位」里该类型的贴图。
   两个按钮共用同一段取值逻辑（`Action<bool> applyTypeUniN`，只差一个 `only` 过滤条件）。
3. 最大边档位加 **4096**：`P2Sizes`（+尾部表项数组必须同序）、`G3SizeItems`、`TexSizeItems`
   共四处；覆盖「一键统一」「部位表/组·部位表」「贴图明细」。
4. 默认预设 = **质量**：`EnsureDefaults()` 生成 4 个预设后写 `.default` 标记；
   `PreReload` 无标记时的回退从"仅优化"改成"先质量、再仅优化"。

### 69.2 又一次"控件被裁"——而且这次是**断言自己先撒了谎**

加了「应用到选中部位」之后，截图里第二个按钮明显被切掉一截。我先加了一个几何断言：

```csharp
int need = c.PreferredSize.Width, have = c.Parent.ClientSize.Width;
... need > have ? " 溢出!" : " 放得下"
```

断言报「放得下」——**和截图矛盾**。原因有两层：

1. 第一版 `have` 用了**父面板**的宽度，而那个父面板是 `AutoSize` 的：宽度恒等于内容宽度，
   比出来永远"放得下"（同义反复）。改成拿**表格的可视宽度**当参照（306/308px）才有意义。
2. 还有一个"未布局"陷阱：父面板还没布局过时宽度是占位值（实测 120），
   这时 `need > have` 会误报溢出。加了 `have <= 200 → 未布局` 的短路。

换成表格宽度做参照后，真因立刻显形：**展开按钮自己占 120px**，和「贴图类型+两个按钮」
那一单元（253px）并排 → 右边界 373 > 可用 306 → 超出 59px。
把展开按钮单独放一行（行面板改 `FlowDirection.TopDown`）后：`需要253 / 表格宽306 放得下` ✓

**教训**：断言本身也要被质疑。"两边都 AutoSize"的比较必然恒真；
用占位值做基准必然误报。**参照物必须是稳定的外部量**（这里是表格宽度）。

排版最终形态（两行，整体不可拆）：
```
[按贴图类型统一 ▾]
贴图类型 [主贴图 ▾]
[应用到所有该类型贴图]  [应用到选中部位]
```

### 69.3 验证

| 项 | 结果 |
|---|---|
| 整卡 vs 选中部位（第 2 页） | 「主贴图」整卡 21 张 / 选中「上衣 top」1 张 |
| 整卡 vs 选中部位（第 3 页） | 「主贴图」整卡 141 张 / 选中组 3 张 |
| 4096 档 | 一键统一 → 部位表出现 `|4096|`；明细表 → `auto/4096`；第 3 页 → `自动 / 4096 / …` |
| 默认预设 | 新装生成 4 个预设 + `.default`；启动类型表 = `maintex=2048 / 其余=1024`（= 质量） |
| 控件放得下 | `需要253 / 表格宽306 放得下`（两页） |
| 界面自检 | 31 场景全部 rc=0、异常=0，含"溢出!"判失败 |
| 产物 | 回归门禁 `SAME=5 DIFF=9`，差异名单哈希与 2.38 相同 |
| 编码 / 占位符审计 | 14 文件干净 / 0 处不一致 |



---

## 70. 1.0：多语言界面（EN/JA/ZH）、改名、版本重置

### 70.1 做法：以中文原文为键的控件树翻译

代码里 200 多处 `Text = "…"` 一行都不用改：

```csharp
public static string T(string zh)          // 中文原文 → 当前语言
public static void Apply(Control root)     // 建完界面后走一遍控件树替换
```

- 字典 `Dictionary<中文原文, [英语, 日语]>`；**没收录的原样返回**（不会变空白），所以可以分批补翻译。
- 下拉项：第一次翻译时把原文留在 `Tag` 里，切换语言从原文重译（否则"中→英→中"会越翻越偏）。
- 反向查表 `ZhOf()`：切语言时 `Text` 里可能是中文原文、英文或日文，三种都要认。
- 运行时填的下拉（贴图类型、统一范围）必须单独包 `L.T(...)`，否则 `Apply` 之后才填进去，翻不到。
- 语言记在 `ui-lang.txt`，默认英语。

### 70.2 踩到的坑（都是"英文比中文宽"引出来的）

| 现象 | 真因 | 修法 |
|---|---|---|
| 单选按钮文字被切（112 需要 / 104 实际） | 换文字后控件没重新测量 | 翻译时对 Button/CheckBox/RadioButton 强制 `AutoSize = true` |
| 窗口标题没翻译 | `ApplyOne` 没把 `Form` 算进带 Text 的类型 | 类型判断加 `Form` |
| 页签名没翻译 | 实际字符串带 ①②③ 前缀，字典里没有 | 按实际字符串补条目 |
| 「显示放大选项」按钮整个不见 | 面板高度是中文时算的；英文多折一行后按钮落在底边外（实测按钮 Y=292 / 面板高 283） | 见下条 |
| 高度怎么设都没用 | **`Dock=Fill/Top` + `AutoSize` 时 `PreferredSize.Height` 只报一行（实测 37）**，而且设完高度再打开 AutoSize 会被它按 37 压回去 | 改成 `Anchor=左右 + AutoSize`，或不给 Dock 并自己算高度 |
| 自己算高度越算越小（283 → 100） | 改高度会改变换行、换行又改变需要的高度 | **迭代到稳定**（最多 6 轮，相等就停） |
| 未选中页的控件被误报截断 | 那些页根本没布局过（子控件 `Visible=false`、高度 0） | 重排跳过不可见控件；CLIP 检查跳过未选中页 |
| 我修的重排反而毁了第 2 页 | 重排时先量了不可见子控件（默认 100 高），照它写回 | 同上：不可见一律不碰；切页（`SelectedIndexChanged`）+ `Shown` + `Resize` 各排一次 |

**核心教训**：`Dock` 和 `AutoSize` 一起用是坑，`PreferredSize` 在换行布局下也不可信；
跨语言的界面必须在**目标语言下**做截断自检（本项目就是 `ui_smoke.sh` 里的 CLIP 断言）。

### 70.3 覆盖范围（诚实说明）

- **已覆盖**：页签 / 分组框 / 标签 / 按钮 / 勾选框 / 单选框 / 表格列头 / 下拉项 / 贴图类型名 /
  窗口标题 / 状态条 / 预览窗 —— 共 200+ 条 × 2 语言（`L.cs` 里 200 条 `Add`）。
- **未覆盖**：日志与状态栏里**运行时拼出来的句子**、部分长 tooltip。形如
  `string.Format("已记住列宽：{0} 个表格 / {1} 列 → {2}", …)` 的模板要逐条改成
  `L.F("…", args)` 才能翻，估计 1000+ 条，留作下一版专项。

### 70.4 验证

| 项 | 结果 |
|---|---|
| 三页截断自检（英语 / 日语） | 均「没有文字被截断」 |
| 界面自检 `ui_smoke.sh` | 31 场景全部 rc=0、异常=0 |
| 默认语言 | 不带 `ui-lang.txt` 启动 = English |
| 语言记忆 | 写 `ui-lang.txt` 后启动按文件走；切换即时生效 |
| 默认输出目录 | `<卡目录>\compressed` |
| 编码 / 占位符审计 | 15 文件干净 / 0 处不一致 |
| 压缩产物回归 | `SAME=5 DIFF=9`，差异名单哈希与 2.39 相同（语言层不动压缩逻辑） |



---

## 71. 1.0.1：切语言崩溃（无限递归）与"布局手术"回退

### 71.1 崩溃根因：事件自触发 → 无限递归 → 段错误

`SetLang` 结尾要把语言下拉框的选中项设回当前语言：

```csharp
cbLang.SelectedIndex = Array.IndexOf(L.All, l);      // ← 这会再次触发 SelectedIndexChanged
```

而 `SelectedIndexChanged` 里就是 `SetLang(...)` → 再次设置 SelectedIndex → …→ **栈溢出**。
表现很吓人：**段错误**（进程被系统直接杀掉），界面上没有任何提示，
因为是栈溢出不是可捕获异常。

定位手法（值得复用）：在可疑函数里插 `Console.WriteLine` 进度标记，看日志走到哪一步断掉：

```
DBG enter → DBG saved → DBG enter → DBG enter → DBG enter …   ← 一眼看出在递归
```

修法：加"程序内切换中"标志 `settingLang`，事件入口与函数入口都判它（同项目里
`settingText` / `syncingCopy` 是同一套路）。

### 71.2 布局整块坏掉：不要跟 WinForms 抢布局

1.0 为了"英文比中文宽"做了几处布局手术，结果第 1 页的「贴图类型」表格整块消失、
左栏被挤成一列。涉及的动作：

- 把 `Dock=Fill` 改成 `Anchor+AutoSize`；
- 把 `pUni1/pUni2/gUni` 的 `AutoSize` 关掉、自己按子控件占位算高度；
- `MakeTypePair` 改成"每个按钮一行"；
- 调 `Relayout` 在 `Shown/Resize/SelectedIndexChanged` 上都跑一遍。

**全部回退**。结论：**宽度不够就别动布局，去把文案改短**。项目的界面是按中文宽度调的，
加语言时最省事、最不容易坏的做法就是"翻译时就按窄栏能放下写"，
再用截断自检（CLIP）兜住。

### 71.3 顺带修的第二个重入

`Relayout` 里设置控件高度 → 触发父容器 `Resize` → 而 `Resize` 上挂着 `Relayout` →
又是无限递归。加了 `relayoutBusy` 重入保护（`try/finally` 复位）。
另外 `Relayout` 跳过 `Visible=false` 的控件：未选中的标签页根本没布局过，
量到的尺寸是占位值，照它写回去会把那一页的布局毁掉（1.0 就吃过这个亏）。

### 71.4 韩语表

- 韩语单独一个文件 `LKo.cs`，键仍是中文原文，**不要求把已有三种语言抄一遍**：
  `Add(zh, en, ja)` 保持原样，韩语用 `K(zh, ko)` 另表存放。
- `T()` 在韩语模式下只查韩语表，查不到就回落中文（可见即可补）。
- **校验脚本** `_work\tex\check_ko.py`：比对"L.cs 的键"与"LKo.cs 的键"，
  数量不一致或键对不上就报出来。本次靠它抓到漏掉的「独占贴图保护」——
  这类漏项在界面上只表现为"那一条还是中文"，很容易漏过去。

### 71.5 验证

| 项 | 结果 |
|---|---|
| 反复切语言（三页 × ko→ja→zh→en→ko） | rc=0、无异常、无段错误 |
| 四语言截断自检 | 三页均「没有文字被截断」 |
| 界面自检 `ui_smoke.sh` | 35 场景全部通过（新增 4 个语言场景 + 2 条硬崩溃断言） |
| 布局对照 | 第 1 页与 1.0 之前截图一致（贴图类型表/统一设置/滑块/预设行都在） |
| 韩语覆盖 | 194 键 / 194 译（校验脚本 0 缺失） |
| 编码 / 占位符审计 | 16 文件干净 / 0 处不一致 |
| 压缩产物回归 | `SAME=5 DIFF=9`，差异名单哈希与 1.0 相同 |



---

## 72. 1.0.2：把日志/状态栏/对话框也纳入多语言

### 72.1 做法：显示时翻译 + 模板化

界面控件文案是构造时翻的（`Apply` 走控件树），但日志/状态栏是**运行时拼出来的**，两种处理：

1. **模板**：`string.Format("中文模板", args)` → `L.F("中文模板", args)`
   （脚本改写，201 处；`AppendFormat` 24 处同理）。这样整句走词条翻译。
2. **拼出来的句子**：`Log("已保存预设：" + path)` 这种，不去改 300 多处赋值，而是在
   **显示入口**翻译：`Log/Log1/Log2/Log3` → `L.Tr(s)`，状态标签换成会自翻译的
   `LocLabel`（override `Text`）→ `L.Tr(value)`。
3. `L.Tr` 的策略：整串命中 → 直接换；否则**从头到尾扫一遍，哪一段能对上词条就翻哪一段**
   （按首字索引 + 长度降序，词条长度 ≥ 4 才参与，避免「材质」这种两字词误伤）；
   结果缓存。

实测最后只剩「卡片自带的部位名」没翻（`"上衣 top"`），那是卡里的数据，**本来就不该翻**。

### 72.2 两个真坑

**① 翻译缓存被多线程写坏 → 程序卡死**
日志是压缩的多个 worker 线程写的，缓存用普通 `Dictionary` 并发写会把内部结构写坏，
症状是**整个程序卡住**。改 `ConcurrentDictionary`。

**② 自检脚本靠中文前缀判断"跑完没" → 看着像卡住**
```csharp
if (s.StartsWith("完成：") || s.StartsWith("已取消")) break;   // 英文界面永远匹配不上
while (sw.ElapsedMilliseconds < 900000) { ... }                // → 空等 15 分钟
```
一共 3 处（扫描 / 部位压缩 / 整体压缩）。改成看"是否还在忙"（`UiBusyNow` /
`UiP2Busy`，语言无关）。**教训：自检脚本不要依赖界面文案，要么用与语言无关的状态标志，
要么把测试语言钉死** —— 这个项目现在 `uitest` 默认强制中文（`MainForm.LangOverride`），
只有显式传 `langforce=` 才换成别的语言。

### 72.3 顺便修的英文布局

- 「独占贴图保护 + 上限」那一行英文要 347px > 这一栏 306px → 把后面的
  「显示放大选项」按钮挤出可视区。**修法是把英文改短**（Protect exclusive / Max），
  不是动布局（1.0 动布局的教训）。
- 「按贴图类型统一」的按钮行允许换行：英文并排 313px > 306px，换行后放得下，中文仍是一行。
- `LocLabel` 加"非 UI 线程不翻译 + 兜底"：跨线程设置控件文字是 WinForms 雷区，
  翻译只应在 UI 线程发生。

### 72.4 验证

| 项 | 结果 |
|---|---|
| 英文日志残留中文 | 仅剩卡片自带部位名（卡数据，不应翻） |
| 四语言 × 三页截断自检 | 全部「没有文字被截断」 |
| 界面自检 `ui_smoke.sh` | 37 场景全部通过（含 2 个英文日志场景） |
| 词条覆盖 | 488 键（界面 194 + 动态 288 + 补漏）· 韩语表 487 · 缺 0 |
| 压缩产物回归 | `SAME=5 DIFF=9`，差异名单哈希与 1.0.1 相同 |
| 编码 / 占位符审计 | 16 文件干净 / 0 处不一致 |



---

## 73. 1.0.3：把"运行时填进去"的中文收干净

界面控件文案是构造时翻的（`Apply` 走控件树），但很多文字是**运行时填进控件/表格**的，
控件树翻译翻不到（或者翻完又被赋值覆盖）：

| 位置 | 处理办法 |
|---|---|
| 表格单元格的取值（跟随部位/自动/不缩放/开/关/不放大…） | `DataGridView.CellFormatting` → `L.Tr(显示值)`：**只翻显示**，底层数据保持中文，逻辑与预设读写不受影响 |
| 类名说明（第 1 页「扫描结果」列，9 条） | 填的时候包 `L.T(Classes.Note(cls))` + 补 9 条词条 |
| 「按贴图类型统一」按钮 / 「贴图类型」标签 | 运行时设文字处包 `L.T` |
| 组过滤的「本体/换1…换7」、第 3 页组名 | 填时包 `L.T` + 前缀规则（`换N`、`换装N`） |
| 预设下拉 | `ComboBox.Format` 事件只翻显示名（`PresetEntry` 对象不动，路径/匹配逻辑不受影响） |
| 预设状态行里的碎片（参数而非模板） | 参数逐个 `L.T` + 补词条 |

### 73.1 新增自检口 `UiCjkLeft`

遍历控件树 + 表格单元格 + 下拉项，列出"**实际显示**仍是中文"的文案。
两个要点：

1. 必须比较 **`L.Tr(原文)` 之后的显示值**。直接看底层数据会大量误报 ——
   表格里存的就是中文原文（显示时才翻），第一版探针因此报了 163 条，实际只有 4 条。
2. 报告要区分"控件文字（界面文案）"和"表格/下拉项（多为数据）"：
   卡片自带的部位名（`上衣 top`）、自定义预设名属于**数据**，本来就不该翻。

### 73.2 结果

英文模式下界面只剩语言下拉里的「日本語 / 中文」（本来就该用各自语言写）。
其余 4 项核对：`UiCjkLeft` 三语 × 三页 = 仅剩语言名；`ui_smoke.sh` 37 场景通过；
词条 509 / 韩语 508 / 缺 0；压缩产物回归 `SAME=5 DIFF=9`（与 1.0.2 同一差异集）。


### 73.3 补：偶发 `Control.WmPaint` 空 DC 异常

切语言（一次要重排+重绘几百个控件）和展开/收起那一栏时，WinForms 偶发：

```
System.ArgumentNullException: Value cannot be null. (Parameter 'dc')
   at System.Windows.Forms.Control.WmPaint(...)
```

默认会被当成"未处理异常"弹框。两个处理：

1. **把界面更新延迟到消息空闲**：`SetLang` / `SetTypeUniVisible` 里的重排改成
   `BeginInvoke(...)`（句柄还没建好时同步做）—— 避免在事件/绘制中间做大规模重排+重绘。
2. **GUI 全局兜底**：`Application.ThreadException` 里记一行日志继续跑，不再弹框。

另外 `UiCjkLeft` 这个自检口有个使用注意：它按**汉字**判断"还没翻译"，
所以对**日语界面无效**（日文本来就用汉字）—— 日语要人工抽查，英文/韩文（韩文用谚文）可以靠它。


---

## 74. 1.0.4：部位名"只保留英文"

卡片里的部位名是 **中文 + 英文 token**（`上衣 top`、`鞋(外层) shoes_outer`、`换装1 School01`、`饰品槽 3`）。
英文/韩文界面下只留英文那截更好读；中文/日文界面不动（汉字对中日读者是有用信息）。

### 74.1 做法：在 `L.Tr` 末尾加一层"部位名剥离"

- **整串就是部位名**（`^[汉字/全角括号/空格]*\s[latin...]$`）→ 只返回拉丁那截。
- **句子里夹着的**（`belongs only to "上衣 top"`、`换装1 School01 · 上衣 top`）→ 用一条
  "汉字若干 + 空白 + 拉丁词" 的正则做片段替换。**只在英文/韩文界面生效** ——
  日文里汉字是正文，做这个替换会把句子搞坏。
- 前缀型名字靠 `RuleAt` 在**逐字扫描**里处理（`换装N`/`换N` → Outfit N、`饰品槽` → Accessory）。
  一开始这两条规则只写在 `T()` 里（整串匹配才生效），结果 `换装1 School01 · 上衣 top`
  这种**句中**出现的换不来 —— 必须挪进扫描循环。

### 74.2 两个坑

1. **词条最小长度要按语言分**：英文/韩文界面下把 `MinFrag` 从 4 降到 2
   （那时界面句子已经是英文，出现汉字基本只剩数据如「身体/脸/头发」；中文/日文保持 4 免得误伤正文）。
   降阈值会让索引缓存失效 —— 切语言时 `ByCache` 也要清。
2. **同一套规则里别把日语词用到韩语上**：`衣装`/`アクセサリ` 是日语，
   韩语要用 `코디`/`액세서리`。第一版写成 `Cur == En ? "Outfit " : "衣装"`，
   韩语界面下就冒出了日文 —— 自检口一眼看出来。

### 74.3 结果

英文/韩文：部位一览只剩英文（`top` / `shoes_outer` / `Outfit 1 School01 · top` / `Accessory 3`）；
整个界面只剩语言下拉的「日本語 / 中文」。中文/日文界面不变。


---

## 75. 1.0.5：先量再优化 —— 切页从 400ms 到 40ms

### 75.1 先造尺子

不看感觉，先加一个自检口：`UiTabBench(n)` 来回切页 n 次，打印**每次切页耗时** +
`L.Relayout` 的累计耗时/次数（`L.RelayoutMs` / `L.RelayoutCalls`）。

第一次量出来：

```
TABBEN : 404ms 416ms 441ms 436ms 435ms 445ms 425ms 483ms 465ms
         | 共 6170ms（平均 685.6ms/次），其中重排累计 3950ms / 9 次
```

**9 成时间在重排** —— 一眼就定位了，不用猜。

### 75.2 三处改动

1. **切页重排只在"这一页第一次显示"时做**（`HashSet<object> relaidPages`）。
   它本来是 1.0.2 为了修英文布局加的，**每次切页都做是浪费**；语言切换时清空标记即可。
2. **重排不再逐控件递归**：原来对树里每个控件都 `PerformLayout() + Invalidate()`，
   其实在**根**上做一次就会级联下去。这一条把单次重排从 ~440ms 降到可忽略。
3. **打开双缓冲**：WinForms 只有 `Form` 默认双缓冲，内部 Panel/TableLayoutPanel/DataGridView
   不是，切页会闪 —— 用反射给整棵树设 `DoubleBuffered = true`（观感上的"卡"很多时候是闪）。

结果：**404~483ms → 39~54ms**，且切语言之后依然稳定（42~54ms）。

### 75.3 顺带修的既有问题

预设状态行太长时右边被**切掉**：`lblPre1` 构造时设了 `AutoSize = false`，
但后面又有一行 `lblPre1.AutoSize = true;` 把它盖掉了。改成可换行（并且把检查器的报错信息
补上 `AutoSize=` 与 `Parent=`，下次定位不用猜）。

### 75.4 验证

| 项 | 结果 |
|---|---|
| 切页耗时（第 2 页 / 第 3 页 / 切语言后） | 39~54ms（优化前 404~483ms） |
| 三语 × 三页截断自检 | 全部「没有文字被截断」 |
| `ui_smoke.sh` | 37 场景全部通过 |
| 词条 / 韩语覆盖 | 518 / 517，缺 0 |
| 压缩产物回归 | `SAME=5 DIFF=9`（与 1.0.4 同一差异集） |


---

## 76. 1.0.6：右上角语言下拉的位置

它原来是 `pLang.Location = new Point(宽 - 面板宽 - 26, 5)` —— 贴在客户区最上面，
和顶部提示条挤在一起，观感上像飘在界面上的孤立控件。

改成与**页签那一行**垂直对齐：`y = tabs.Top + max(0, (页签高 + 8 - 面板高) / 2)`（实测 5 → 30）。

### 76.1 一个必须注意的点

`place()` 在**构造时**调用，而那时控件还没布局 —— `tabs.Top`、`lblDrop.Bottom`
读到的都是 **0**，于是面板被摆到了 y=0（比原来更靠上，飘到标题栏下面去了）。
所以：`Resize` + `Shown` + `tabs.Layout` 三处都要重摆一次，
另外加了个自检口 `UiLangPanelRect()` 直接打印面板与页签的实际坐标 ——
这类"位置不对"的问题，量坐标比看截图可靠（用 `DrawToBitmap` 做快照时，
这种浮在控件之上的兄弟控件有时根本画不出来，看截图会误判成"没显示"）。


---

## 77. 1.1.0：外观主题（档 1 + 1.5）

### 77.1 做法：又是一次"一套表 + 遍历控件树"

```
Theme.Set(Light/Dark)   // 一张颜色表
Theme.Apply(root)       // 遍历控件树按类型套色（和不语言那套一个套路）
Theme.ApplyNative(...)  // 句柄建好后处理系统部分：深色滚动条 + 深色标题栏
```

按类型分别处理（这是关键，不能只设 `BackColor` 了事）：

| 类型 | 处理 |
|---|---|
| Form / Panel / TabPage / GroupBox | 背景 + 前景 |
| DataGridView | `BackgroundColor`、`GridColor`、`ColumnHeadersDefaultCellStyle`、`DefaultCellStyle`、`AlternatingRowsDefaultCellStyle`、`RowHeadersDefaultCellStyle`、下拉列的 `DefaultCellStyle` |
| ComboBox | **必须 `FlatStyle = Flat`** —— 否则系统主题绘制会忽略 `BackColor`（深色下就是一块白） |
| Button | `FlatStyle = Flat` + `FlatAppearance` 悬停/边框；主按钮（`Tag = "primary"`）用强调色实心 |
| TextBox / RichTextBox | 背景 + 前景 + `BorderStyle` |
| Label | 默认前景；**原本是灰色的说明文字保持次要色**（用颜色值判断） |
| CheckBox（按钮样式） | 像按钮一样跟着强调色 |

### 77.2 档 1.5 的三件事

1. **页签自绘**（`ThemedTabControl`，`DrawMode = OwnerDrawFixed`）：系统页签深色下不搭；
   选中项画强调色下边线，未选中用次要色。
2. **滑块自绘**（`ThemedSlider`）：`TrackBar` 是系统主题画的、配色改不了。
   自己实现 `Minimum/Maximum/Value/ValueChanged`（**保持与 TrackBar 相同的用法**，
   调用处一行都不用改），画圆角轨道 + 强调色填充 + 圆形滑块，支持拖动与滚轮。
3. **深色标题栏 / 滚动条**：`DwmSetWindowAttribute(hwnd, 20/19, 1)`（实测返回 0 = 系统接受）+
   `SetWindowTheme(hwnd, "DarkMode_Explorer")`。这两件事**必须在句柄创建之后**做，
   所以放在 `Shown` 里再套一次。

### 77.3 踩到的坑

- **系统绘制的控件不认颜色**：ComboBox（DropDownList）、TrackBar、ProgressBar、页签 ——
  前两个分别用 `FlatStyle = Flat` 和自绘解决；后两个（进度条等）留在"改不了"清单里。
- **`DrawToBitmap` 截图不可靠**：用它做的快照里，文本框仍按系统色渲染，
  看上去像"主题没生效" —— 实际是快照的问题。所以**主题验证改用自检口直接读颜色**：
  ```
  THEME : 主题=Dark 窗体=FF202022 txtIn=FF1A1A1C grid表头=FF343439 主按钮=FF4884E0 标题栏深色API=0
  ```
- 想抓"真实窗口"截图验证时，`SetForegroundWindow` 受 Windows 前台锁定限制会失败，
  截到的是别的窗口 —— 别再在这上面浪费时间，量颜色更快更准。

### 77.4 结果

| 项 | 浅色 | 深色 |
|---|---|---|
| 窗体 | #FFFFFF（原来是 #F0F0F0 的灰） | #202022 |
| 输入框 | #FCFCFD | #1A1A1C |
| 表头 | #F3F5F8 | #343439 |
| 页签 | #FFFFFF | #202022 |
| 主按钮 | #266CCC | #4884E0 |
| 标题栏深色 API | — | 返回 0（系统接受） |

改不了的：MessageBox、打开文件对话框、进度条等系统组件。


---

## 78. 1.1.1：两个主题相关的坑

### 78.1 `UserPaint` 让自绘页签变成空白

为了页签跟主题走，我把 `TabControl` 换成 `ThemedTabControl` 并加了自绘。第一版构造函数里写了：

```csharp
SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);   // ← UserPaint 是错的
DrawMode = TabDrawMode.OwnerDrawFixed;
```

`UserPaint` 的含义是"**整个控件由你自己画**"，所以框架不再画页签文字 —— 页签条变成一条空白
（用户："最上面选择压缩模式的那一栏位消失了"）。**只保留 OwnerDrawFixed 就够了**，
它只接管"每个页签的绘制"，其余（条带背景等）框架照画。

> 注意：这类"真实窗口不画、但 `DrawToBitmap` 快照里有"的差异，看快照会误判成"没问题" ——
> 这也是我一开始没发现的原因。**验证要看真实窗口**。

### 78.2 深色标题栏 ≠ 深色边框

只设 `DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE=20/19, 1)` 时，
标题栏会变深，但**窗口边框往往还是白的**（用户反馈）。Win11 22000+ 还要另外指定：

```
DWMWA_CAPTION_COLOR = 35   // 标题栏那一条的颜色
DWMWA_BORDER_COLOR  = 34   // 边框颜色
```

注意 DWM 要的是 **COLORREF（0x00BBGGRR）**，不是 .NET 的 ARGB；
浅色时要传 `DWMWA_COLOR_DEFAULT (0xFFFFFFFF)` 恢复系统默认。

### 78.3 截图验证的教训（重要）

- `DrawToBitmap`：主题色不可信（文本框按系统色渲染），且**看不出真实窗口的绘制问题**（见 78.1）。
- `CopyFromScreen`：需要窗口在前台，`SetForegroundWindow` 受 Windows 前台锁定限制会失败 ——
  结果是截到了别的窗口（我连续两次截到了浏览器和视频）。
- 比较靠谱的是 **`PrintWindow` + `PW_RENDERFULLCONTENT(2)`**：不依赖前台，
  能抓到真实渲染（我用它确认了页签文字恢复）。但它仍可能抓到**残留的旧进程窗口** ——
  抓图前先 `Stop-Process -Name <进程>` 清干净。
- 最快的还是**自检口直接读值**（主题颜色、API 返回码、控件状态），不信界面截图。



---

## 79. 1.1.2：扁平下拉格的"边框消失"

现象：贴图类型表的「处理方式」「最大边」两列边框与背景融成一片，看不出是下拉框。

根因链（都是主题化带出来的）：

1. `DataGridViewComboBoxColumn` 若不设 `FlatStyle = Flat`，就由**系统主题**绘制 → 深色下是白底（1.1.0 的问题）。
2. 设了 `Flat` 之后颜色听话了，但**扁平样式同时去掉了边框** → 变成"白底 + 一行字"（本次问题）。

修法：颜色继续用 Flat 控制（`DefaultCellStyle.BackColor` 用**专用底色** `CellInputBg`，
比表格底色分别略灰/略亮一档），边框改成**自己画**：

```csharp
g.CellPainting += (s, e) => {
    if (e.RowIndex < 0 || !(g.Columns[e.ColumnIndex] is DataGridViewComboBoxColumn)) return;
    e.Paint(e.CellBounds, ... 各 PaintParts ...);        // 先按正常画（含下拉箭头）
    e.Graphics.DrawRectangle(new Pen(Theme.CellInputBorder), 内缩 1px 的矩形);
    e.Handled = true;
};
```

要点：
- 必须把 `ContentForeground` 也算进 `e.Paint(...)`，否则**下拉箭头会不见**。
- 用 `g.Tag` 打标记避免重复挂钩（`Apply` 会在切主题/切语言时反复跑）。
- 只对下拉列生效，其它格不动。

主题里为此新增了两个颜色：`CellInputBg`、`CellInputBorder`
（浅色 F6F8FB / BCC0C8，深色 303035 / 5C5E64）。

### 79.1 又一次"别只看快照"的印证

`DrawToBitmap` 的快照里这类单元格是系统色渲染的，看不出边框有没有；这次的确认用的是
`PrintWindow`（真实窗口渲染）—— 就是上一版为了确认页签才换上的手段。


---

## 80. 1.1.3：真下拉框的边框 + 底栏换行被裁

### 80.1 扁平样式会连边框一起去掉（第二次踩）

`ComboBox`（DropDownList）不设 `FlatStyle = Flat` 时由系统主题绘制、忽略 `BackColor`；
设了 Flat 颜色听话，但**边框没了** → "边框和背景融为一体"。

- **表格下拉列**：用 `CellPainting` 自绘边框（1.1.2，只能自绘）。
- **普通下拉框**：做了一个 `ThemedCombo`，在 `WndProc` 收到 `WM_PAINT` 后补一圈边框。
  但**每个控件每次重绘都要 `Graphics.FromHwnd`**，一个页面几十个下拉框 → 切页从 40ms 涨到 90ms。
  **折中**：浅色主题用 `FlatStyle.Standard`（系统自带边框、零开销），深色才 Flat + 自绘。
  实测：浅色 45~58ms（不变），深色 65~106ms（可接受）。

### 80.2 `Dock=Fill` + `AutoSize` 又坑了一次

底部动作条 `pAct` 是 `AutoSize = true, Dock = DockStyle.Fill`。窗口变窄 → 按钮换行到第二行 →
**容器高度不跟着涨** → 第二行被裁（用户："记住列宽/恢复默认列宽被挤到主按钮下面"）。
改成 `AutoSizeMode = GrowAndShrink` + `Anchor = Top|Left|Right`（不要 Dock）即可，
外层 AutoSize 行就会跟着变高。

### 80.3 补了"窄窗口"自检

在这个 bug 出现之前，自检只在默认窗口尺寸（1360×942）下做截断检查，
窗口一窄就没人管。现在第 2/3 页自检里加了：

```
UiResize(1050, 900)  →  UiClippedText()
```

顺手暴露出另一个**既有**问题（不是本次引入）：极窄窗口下中栏只剩 ~160px，
「显示放大选项」按钮被挤出面板 —— 根因是 WinForms 对会换行的容器**缓存了 PreferredSize**，
尺寸变化后不重算（我试过在 Resize 里重排、也没用）。留在"已知遗留"里，
要根治得把那两行拆成独立行（改布局，需用户确认）。


---

## 81. 1.1.4：精简界面（去重复入口）

用户提出三件事，都是"减"：

1. 中文的「灰度用 2 通道（同一件事）：R==G==B 的贴图只存一个通道，省掉重复的两份。」
   → 缩成 **「灰度用 2 通道（R=G=B）」**（与 en/ja/ko 一致）。
   注意点：**字典的键就是中文原文**，所以改文案必须同时改
   `L.cs` 里的键和代码里的字面量，否则翻译会失效（这里两处都改了）。
2. 删「单人物卡压缩」页的「批量：用当前设置处理选中的卡」——
   它与主按钮「压缩人物卡」调用**同一个方法**（`G3Run()`），是重复入口。
3. 删「单服装卡细分压缩」页的「用当前设置批量处理文件夹…」——
   批量入口在第 1 页已有，这一页重复。

### 81.1 验证手法

删除/改名这类改动，用自检口 `findtext=<文字>` 直接确认最省事：

```
findtext=用当前设置批量处理文件夹…   → 没找到 ✓
findtext=批量：用当前设置处理选中的卡 → 没找到 ✓
findtext=灰度用 2 通道（R=G=B）      → CheckBox {...} ✓（旧长文案：没找到 ✓）
```

### 81.2 取消按钮的说明（用户问到的）

第 2/3 页底部的「取消」是**中止正在进行的压缩**：平时灰着（`Enabled=false`），
点开始压缩后才变亮；按下后置 `cancelFlag`，当前这张处理完就停，已产出的文件不回滚。
不是重复按钮，保留。


---

## 82. 1.1.5：气泡提示（tooltip）的多语言 + 白话化

### 82.1 为什么气泡一直没被翻译

`L.Apply` 是**遍历控件树**替换 `Text` 的，而 tooltip 存在 `ToolTip` 组件里、**不在控件树上** ——
所以从多语言上线起，气泡一直是中文（早期就标注为"未覆盖"）。

做法：登记"怎么把译文设回去"的动作，切语言时统一重放：

```csharp
static readonly List<KeyValuePair<Action<string>, string>> TipReg;
public static void Tip(Control c, string zh) { ...; TipHost.SetToolTip(c, T(zh)); }
public static void Tip(DataGridViewColumn col, string zh) { ...; col.ToolTipText = T(zh); }
public static void RetipAll() { foreach (...) kv.Key(T(kv.Value)); }
```

把 46 处 `new ToolTip().SetToolTip(...)` / `tipX.SetToolTip(...)` 机械替换成 `L.Tip(...)`
（脚本前缀替换即可，参数原样保留）。**表格列**不是 `Control`，所以另开一个重载走 `ToolTipText`。

### 82.2 踩的坑：气泡在任何语言下都显示英文

构造界面时 `L.Cur` 还是**静态默认值**（`Lang.En`），而"设置当前语言"的语句在构造**之后** ——
于是 `L.Tip(...)` 在构造期就把英文写进了 `ToolTip`，启动时又没有任何一步重设它。
表现：中文/日文/韩文界面下气泡全是英文。

修法：构造函数里 `L.Apply(this)` 之后补一行 `L.RetipAll();`。
**规律**：凡是"在构造期就落地、且依赖当前语言"的东西（气泡、缓存、格式化文本），
都要在设好语言之后重放一次。

### 82.3 说明文字白话化

原来的气泡是一长段术语 + 实测数字（"细节保持+23%、光照误差 −18%…"），
改成一句话说清"它做什么、不勾会怎样"：

| 选项 | 新说明（中文） |
|---|---|
| 细节图保细节 | 降分辨率时用面积平均，细节更清楚。不勾 = 用普通缩放（更快）。 |
| 彩色贴图也走面积平均 | 彩色贴图也一起用面积平均 + 边缘锐化，观感更好。 |
| 独占贴图保护 | 只被一个部位用到的贴图不跟着降 —— 它往往是那个部位唯一的高清来源。 |
| 灰度用 2 通道 | 三通道相同的灰阶图只存一个通道：体积更小，像素完全不变。 |

四段文字各配 en/ja/ko 三条词条（键仍是中文原文）。

### 82.4 验证

自检口 `UiTipProbe()` 直接读四个控件的气泡文字，按语言逐个核对：

```
[zh] 降分辨率时用面积平均，细节更清楚。不勾 = 用普通缩放（更快）。
[en] Area averaging when downscaling — keeps detail clearer. Unchecked: plain resize (faster).
[ja] 縮小時に面積平均を使います（細部がきれい）。オフ: 通常の縮小（速い）。
[ko] 축소 시 면적 평균 사용 (디테일 선명). 해제: 일반 축소 (더 빠름).
```


---

## 83. 1.1.6：全部气泡白话化 + 覆盖率自检

### 83.1 覆盖范围

1.1.5 只改了四个压缩选项；这一版把其余 29 条也改完（共 33 条文案 / 66 处调用，
有些文案在多页共用）。原则：**一句话说清"做什么、什么时候用"**，
删掉实测数字与算法参数表（例：放大算法从一张 SSIM/锐度/振铃对照表 → 一句
"默认 lanczos3 最清晰，mitchell 最柔和，nearest 只适合像素画"）。

### 83.2 新增覆盖率自检 `L.MissingTips()`

```csharp
// 当前语言下，译文 == 中文原文 的，就是"没进词条"的
if (T(zh) == zh && Cur != Lang.Zh) miss.Add(zh);
```

输出：`气泡共 66 条（去重 42），本语言未翻译 0 条：（全部已翻译）`。
**这个检查比逐条肉眼看快得多** —— 改完文案后先跑它，就知道还差哪些词条。

### 83.3 两个操作教训（都是"插入点"问题）

1. **批量替换多行调用**：气泡文字常常是 `"第一段" + "第二段"` 跨多行，
   用"从 `L.Tip(控件,` 到第一个 `);`"整段替换才安全（`rewrite_tips.py` 就是这么做的）。
2. **往词条表里追加班块时，插入点要落在"一条完整词条之后"**：
   我按"最后一行以 `K("` 开头"定位，结果插到了**某条词条的中间**（它的译文在下一行），
   直接语法错误。正确定位是"最后一行以 `);` 结束"。


---

## 84. 1.1.7：切语言后"表格单元格不跟着变"

### 84.1 根因：两种文字的实现方式不同

| | 实现 | 切语言时 |
|---|---|---|
| 标签 / 按钮 / 勾选框 | 直接改 `Text` 属性 | 立刻重画 ✓ |
| **表格单元格** | **绘制时**翻译（`CellFormatting` 里逐格 `L.Tr`） | 只改属性不会触发重画 ✗ → 停在旧语言 |

用户看到的正是这个差异：旁边的按钮变英文了，格子里还是中文。

修法：切语言后主动让四个表格失效重画：

```csharp
void InvalidateGrids() {
    foreach (var g in new[]{ gridParts, gridTex, gridG, gridGT }) {
        if (g == null || g.IsDisposed) continue;
        g.Invalidate();
        try { g.InvalidateColumn(0); } catch { }   // 下拉格由编辑控件绘制，缓存更久
    }
}
```

**一般规律**：凡是"绘制时才决定显示文字"的东西（`CellFormatting`、自绘控件），
状态变了都要显式 `Invalidate`，不能指望它自己重画。

### 84.2 验证：截图不可靠时改用计数

这台机器上：
- 外部抓屏（`CopyFromScreen`）需要窗口在前台，`SetForegroundWindow` 受前台锁定限制 → 抓到别的窗口；
- 进程内 `PrintWindow` + `PW_RENDERFULLCONTENT` 也不可靠 —— **实测返回的是切换前的陈旧画面**
  （同一进程内探针读到 `主按钮=Compress this outfit card`，而快照里还是中文），
  我一开始就被它误导过一次。

于是改用**计数**：在 `CellFormatting` 里累加 `CellFmtCount`，切一次语言看涨多少：

```
切 en → 单元格重绘 97 次
切 ja → 85 次
切 ko → 85 次
```

涨了 = 这批格子确实重新翻译并重画了 —— 直接对应"用户看到的那些格子"。

### 84.3 顺带修的：自检脚本没泵消息

`SetLang` 自 1.1.0 起改成 `BeginInvoke` **延迟执行**（避免绘制期异常）。
自检脚本调用 `SetLang` 后立刻读状态 / 截图 → 读到的全是**切换前**的值。
现在自检在切换后会 `DoEvents + Sleep(120) + DoEvents` 再读。


---

## 85. 1.1.8：表头不跟着切语言（`T()` 传了译文）

### 85.1 根因：翻译函数只认中文原文

表头和单元格是**两条不同的路径**：

| | 何时翻译 | 用什么 |
|---|---|---|
| 表格单元格 | 每次**绘制**时（`CellFormatting`） | `L.Tr(原文)` ✓ 幂等 |
| 表头 | 切语言时**翻一次** | `T(表头当前文字)` ✗ |

`T(zh)` 的语义是"**中文原文** → 当前语言"，传入不是键的文字就**原样返回**。
于是表头的命运是：中文 →（切 en）→ 英文 ✓ →（切 ja）→ `T("On")` → 不是键 → 仍是 "On" ✗ **卡住**。

修法：加一个反查接口（标签一直在用 `ZhOff` 反查，表头当初漏了）：

```csharp
public static string Retranslate(string shown) {
    string zh = ZhOf(shown);            // 译文 → 中文原文
    return zh != null ? T(zh) : shown;  // 再按当前语言翻
}
```

表头改用它。**一般规律**：凡是"会被反复重设"的文字，翻译必须**幂等**
（要么每次从原文翻，要么先把译文反查成原文再翻）；只有"每次都用中文原文重新赋值"的场景才能直接用 `T()`。

### 85.2 审计

顺手把其余 8 处 `L.T(...)` 全查了一遍：预设名、类名（`Classes.Short/Label/Note`）、
组名（`Chara.GroupName`）、组过滤短标签——**传的都是中文原文** ✓，
只有表头喂的是译文。现在这条隐患清零。

### 85.3 验证

自检口逐次切换并回读四个表格的表头：

```
切 en → [On|Part] [TexID|Type] [On|Group · Part] [TexID|Type]
切 ja → [有効|部位] [TexID|種別] [有効|組 · 部位] [TexID|種別]
切 zh → [启用|部位] [TexID|类型] [启用|组 · 部位] [TexID|类型]
切 ko → [사용|부위] [TexID|종류] [사용|그룹 · 부위] [TexID|종류]
```

**教训**：上一版只修了"单元格不重画"，没查"表头用的是哪个翻译函数" ——
两个问题症状相同（切语言后不跟着变），但根因完全不同。
排查这类问题时，要按"这块文字是**什么时候、用什么函数**翻译的"分类，而不是按症状。
