# Koikatsu 服装 mod（zipmod）贴图 解包 → 读贴图 → 压缩 → 重打包

> 本机实测通过（2026-09-14）。样例：`[Fr]SY outfit-v1.01.zipmod` → `_work\out\SY-tex1024.zipmod`
> 结果：**13,181,692 B → 5,039,116 B（38.2%）**；贴图显存 **89.3 MB → 12.0 MB（-86.6%）**；10 张贴图全部 1024²，格式与 mip 链正确。

---

> 说明：本文是开发期的实测记录，文中的路径为当时的工作区结构，仅供参考。

## 0. 先厘清概念（很重要）

| 你以为的 | 实际是 |
|---|---|
| 「服装卡」.png（角色卡/坐标卡） | 里面**只有配件 ID + 颜色数值**，**不含任何贴图**，无法"解包出贴图" |
| 服装 mod `.zipmod` | 贴图真正所在：`abdata/chara/*.unity3d`（Unity **5.6.2f1** AssetBundle） |

要动贴图，动的就是 **zipmod**。

还要分清两种"大小"：
- **显存/内存**：由贴图尺寸 + 格式决定。4096² DXT5 带 mip = **21.3 MB 显存**（无论文件多大）。
- **文件体积**：由 bundle 内部压缩方式（LZ4 / LZMA）+ 贴图冗余度决定。本例 `a`、`cailai` 两张 4096 贴图近乎纯色，22 MB 原始数据在 bundle 里只占约 1 MB。

---

## 1. 工具（本机已全部就绪，全离线）

| 用途 | 路径 |
|---|---|
| 读写 bundle（**唯一能写**的） | `D:\soft\KoikatsuModdingTools-master\Tools\SB3UGS\`（`SB3UtilityGUI.exe` / `SB3UtilityScript.exe` / `plugins\UnityPlugin.dll`） |
| 只读查看/导出（无 CLI） | `D:\soft\AssetStudio.net6.v0.16.47\AssetStudioGUI.exe` |
| 缩放 + DXT 编码 | `C:\Python313\python.exe`（Pillow 12.1.1，实测可写 DXT1/DXT5/BC3 DDS） |
| 必须的宿主 | **32 位 PowerShell**：`C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe`（SB3UGS 程序集是 x86） |
| 改材质/着色器时才需要 | Unity 5.6.2f1：`D:\soft\unity\Editor\Unity.exe` |

---

## 2. 一条命令跑完（推荐）

```powershell
& 'C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe' -NoProfile -ExecutionPolicy Bypass `
  -File '_work\tex\shrink_zipmod.ps1' `
  -Zipmod '[Fr]SY outfit-v1.01.zipmod' `
  -OutFile '_work\out\SY-tex1024.zipmod' `
  -MaxSize 1024
```

参数：`-MaxSize`（长边上限，1024 最常用）、`-Level`（0=不压缩 / 1=LZ4 / 2=LZ4HC / ≥3=LZMA，默认 3）、`-Skip`（默认不缩的遮罩贴图正则）。

脚本流程（每个 `.unity3d` 一遍）：

```
zipmod 解压（结构保持：abdata/ + manifest.xml 在根）
 └─ SB3UGS 打开 bundle
     ├─ 枚举 Texture2D（classID1 == 28），LoadWhenNeeded 取名字/尺寸/格式
     ├─ ExportTexture → <名字>-DXT5.dds
     ├─ Pillow：LANCZOS 缩到长边 ≤ MaxSize → <名字>.png（保留 alpha）
     ├─ editor.MergeTexture(png)   ← SB3UGS 按原格式重新编码 + 自动重建完整 mip 链
     └─ editor.SaveUnity3d(就地, LZMA) → 立刻重开校验
重打包为 .zipmod（UTF-8 目录项，根目录直放 abdata/ 与 manifest.xml）
```

相关脚本：
- `_work\tex\shrink_zipmod.ps1` —— 主流水线
- `_work\tex\prep_tex.py` —— DDS → 缩放 → PNG
- `_work\tex\sb3_inventory.ps1 -Dir <abdata\chara>` —— 贴图盘点（名字/尺寸/格式/mip/原始字节数）
- `_work\tex\compare_tex.py` —— 压缩前后像素差异对比

---

## 3. 实测结果

| 贴图 | 所属 bundle | 之前 | 之后 |
|---|---|---|---|
| `a` | FM_SYTop | 4096² DXT5 13 mips 21.3MB | 1024² DXT5 11 mips 1.33MB |
| `cailai` | FM_SYLegwear / FM_SYShoes | 4096² DXT5 | 1024² DXT5 |
| `[UV] Cloth_Base Color` | FM_SYbot | 2048² DXT1 | 1024² DXT5 |
| `Sera_Yura_ACC` / `_Normal` | FM_SYAcc | 2048² DXT5 | 1024² DXT5 |
| `Sera_Yura_ACC_AO` / `_Mask` | FM_SYAcc | 1024² DXT1 | **未动**（遮罩） |
| `Socks_..._Fishnet 拷贝` / `Skirt_Base_Chidori_Black 拷贝` | FM_SYLegwear | 2048² DXT5 | 1024² DXT5 |

- zipmod：13,181,692 → 5,039,116 B
- 贴图原始字节合计（≈显存）：93,673,016 → 12,583,152 B
- 像素校验（新 bundle 导回 DDS vs 原图降采样）：内容纹理 MAD 0.6~3.5 / 255，遮罩 0.00（完全未变）

---

## 4. 手工 GUI 版（不想跑脚本时）

1. zipmod 改后缀 `.zip` → 用 Bandizip/WinRAR 解压到工作目录（保持 `abdata\chara\*.unity3d` 结构）。
2. 双击 `D:\soft\KoikatsuModdingTools-master\Tools\SB3UGS\SB3UtilityGUI.exe`。
3. `File → Open` 打开 `abdata\chara\FM_SYTop.unity3d`，左侧选 AssetBundle 节点 → 右侧 **Unity3d** 页签列出全部资源。
4. 选中 Texture2D → **Export**（`a-DXT5.dds`）。
5. 用 Photoshop/画图/Pillow 把图缩到 1024（保持 2 的幂），**另存为 PNG**。
6. 回到 SB3UtilityGUI → **Replace** → 选刚存的 PNG（SB3UGS 会按原格式重编码并生成完整 mip）。
7. **Save**（写回同一 bundle）。若报 `Resource file must be placed into a folder with the original folder structure!`，说明你点的是 SaveMod/带路径的保存项——改成就地 Save。
8. 重新打包：把 `abdata\`、`manifest.xml`（以及任何原文件）一起压成 zip，后缀改回 `.zipmod`（**根目录不能多一层文件夹**）。
9. 覆盖进游戏 `Mods\`，重启游戏验证。

> 图形化操作会被 SB3UGS 记录到 `Tools\SB3UGS\SB3UtilityGUI.autosavescript.txt`，可当脚本模板复用。

---

## 5. 关键坑（都踩过）

1. **回灌必须用 PNG，不要用多级 mip 的 DDS**：SB3UGS 导入 PNG 会自己按原格式（DXT1/DXT5）重编码并**重建完整 mip 链**；而手写多 mip 的 DDS 会被**静默拒绝**（贴图尺寸不变、无报错）。单 mip DDS 能进，但会把 `m_MipCount` 设成 0（无 mip → 远处闪烁）。
2. **保存用 5 参或 7 参重载**（`SaveUnity3d(keepBackup, backupExtension, background, clearMainAsset, pathIDsMode [, compressionLevel, compressionBufferSize])`，**就地写回**）。带 `path` 的 8 参重载**必抛** `Resource file must be placed into a folder with the original folder structure!`。
3. **必须用 32 位 PowerShell**，且把 `SB3UGS\*.dll` 与 `SB3UGS\plugins\*.dll` **全部预加载**；否则报 `Could not load file or assembly 'LZ4, Version=1.0.10.93'`。
4. **遮罩贴图不要缩**：`*_mc`、`*_ml`、`*_mab`、`*_Mask`、`*_AO` 参与 alpha 裁剪 / 材质遮罩，缩小会让边缘变糊、材质异常。脚本默认按 `-Skip` 正则跳过。
5. **法线贴图** `*_n` / `*_Normal` 可以缩，但 DXT5 压缩噪点会直接体现在光照上；2048→1024 通常可接受，别再往下。
6. 文件名含 `[` `]` 的贴图（如 `[UV] Cloth_Base Color`）在 PowerShell 里会被当通配符 → 一律用 `-LiteralPath`。
7. **保持 `manifest.xml` 原样**（GUID 不变就还是同一个 mod，已有服装卡/坐标卡不会失效）。脚本只改 bundle，manifest 字节级不变。
8. 改完**重启游戏**（bundle 有缓存），并用「服装 → 选中该部位 → 看贴图/颜色是否正常」验证。
9. 若 `-Level 3`(LZMA) 的 bundle 在游戏里加载异常（概率很低），改 `-Level 2`(LZ4HC) 重跑即可，体积会大一些（本例 Top：513KB → 944KB）。

---

## 6. 自检清单

- [ ] 重开 bundle 打印贴图表：尺寸/格式/mip 是否如预期（脚本自带 verify 行）
- [ ] `compare_tex.py` 像素差异 MAD 是否在个位数
- [ ] 新 zipmod 根目录只有 `abdata\`、`manifest.xml`，`manifest.xml` 与原文件字节一致
- [ ] 进游戏实测：服装外观一致、无闪烁、无贴图错位

---

# 附：服装卡（.png）里**确实**有贴图 —— KoiKatu 衣服卡（MaterialEditor）解包

`CardA.png`（177,381,281 B）不是普通角色卡，而是**衣服卡**：
PNG 缩略图 + IEND 之后追加的 **KoiKatuClothes** 载荷（177,237,602 B）。

## 格式（已完整逆向）

```
[PNG 缩略图 252x352 RGBA，到 IEND 为止]
[payload @143679]
  int32 LE(100) | len+str "【KoiKatuClothes】" | len+str "0.0.0" | len+str 卡名 | 8 bytes
  MessagePack 文档①：{version, parts[9](id/colorInfo/emblemeId/hideOpt/sleevesType), ...}   @143728..149655
  后续 MessagePack 文档：配件数据（typex/id/parentKey/addMove/color/hideCategory/noShake）
  ...
  "...com.deathweasel.bepinex.materialeditor" -> array[0, map(10)]：
      TextureDictionary            bin32(176,806,529)  ← 内部又是 msgpack：map<TexID, bin32(PNG)>，34 张贴图
      RendererPropertyList         bin(586)    6 条渲染器开关
      MaterialFloatPropertyList    bin(293,841) 2,752 条浮点属性覆盖
      MaterialColorPropertyList    bin(108,657) 769 条颜色属性覆盖
      MaterialTexturePropertyList  bin(10,877)  74 条「材质+属性 → TexID」绑定
      MaterialShaderList           bin(2,848)   18 条着色器替换（KKS/lilToon/Opaque ← Shader Forge/main_alpha）
```
> 8 字节神秘区（本例 `10 2D 00 00 1F 17 00 00`）跳过即可，脚本用 `\xa7version` 回定位。

## 解包

```powershell
python _work\tex\kkcard_extract.py "CardA.png" _work\kkcard\textures
```
实测抽出 **34 张 PNG，共 176.8 MB**：4096²×19（RGBA/RGB/L/LA）、2048²×13、512²×2。

## 压缩 + 重新打包成卡

```powershell
python _work\tex\kkcard_repack.py "CardA.png" "_work\kkcard\out\White&Deep_Blue 2 (tex1024).png" 1024
```
做法：只把 `TextureDictionary` 这个 bin 换成重新序列化的 msgpack（含缩小后的 PNG），
载荷其余部分（衣物数据、配件数据、材质属性列表、缩略图）**原样搬运**，因此格式不会破。
实测：**177,381,281 → 19,013,639 B（169 MB → 18 MB，-89%）**，34 张贴图全部 ≤1024，
重新解析后：衣物文档/部件 ID/34 个 TexID/尾随文档全部一致，缩略图正常。

其它工具：`kkcard_msg.py`（结构查看）、`kkcard_material.py`（材质/贴图绑定表）、
`kkcard_scan.py`（内嵌文件签名扫描）、`hexdump_at.py`（定点十六进制查看）。
