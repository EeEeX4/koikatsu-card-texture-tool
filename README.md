<div align="center">
  <img src="assets/icon.png" width="128" alt="icon">

  # Koikatsu Card Texture Tool

  **Compress the textures inside Koikatsu coordinate / chara cards.**
  恋活（コイカツ）衣装カード・キャラカードのテクスチャを圧縮するツール。
  코이카츠 의상 카드 / 캐릭터 카드의 텍스처를 압축하는 도구.

  [![build](https://github.com/EeEeX4/koikatsu-card-texture-tool/actions/workflows/build.yml/badge.svg)](../../actions)
  ![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
  ![.NET](https://img.shields.io/badge/.NET-8-512BD4)
  ![license](https://img.shields.io/badge/license-MIT-green)

  [中文](#中文) · [English](#english) · [日本語](#日本語) · [한국어](#한국어)
</div>

> [!CAUTION]
> ## ⚠️ 使用前请备份原卡！
> 本工具会**重写卡片里的贴图**。请先复制一份原始卡片再压缩 —— 万一效果不满意，原卡还在，随时可以重来。
>
> **Back up your cards before using this tool.** It rewrites the textures inside them — always keep the originals.
>
> **ご使用前にカードを必ずバックアップしてください。** 本ツールはカード内のテクスチャを書き換えます。
>
> **사용 전에 카드를 반드시 백업하세요.** 이 도구는 카드 안의 텍스처를 다시 씁니다.

---

## 中文

### 这是什么

把 Koikatsu 的衣服卡 / 人物卡里的贴图**重新压缩**，让卡片体积变小（常见能减 50%~80%），画质损失尽量小。自带图形界面，也有命令行。

**三种模式**

| 页面 | 用途 |
|---|---|
| 批量压缩 | 一次处理整张卡或整个文件夹 |
| 单服装卡细分压缩 | 逐个部位、逐张贴图分别设格式与最大边 |
| 单张人物卡压缩 | 按「组 · 部位」细分处理人物卡 |

**主要特性**

- **主贴图 / 其它贴图分开处理**：法线、掩罩这类贴图通常不需要高分辨率
- **无损失选项**：PNG 无损再压缩、JPEG 最优哈夫曼、灰度图降成 2 通道（像素完全不变）
- **独占贴图保护**：只被一个部位引用的贴图不跟着降分辨率
- **预设**：调好一张卡后导出，套用到同款服装的其它卡上
- **四种界面语言**：中文 / English / 日本語 / 한국어；浅色 / 深色主题
- 支持 HDR 环境反射贴图（原样保留或按需缩放）、8192px 大图、Alpha 遮罩

### 下载

到 [Releases](../../releases) 下载：

| | 说明 |
|---|---|
| `KoiCardTexTool.exe`（自包含，约 66 MB） | **推荐**，解压即用，不用装任何运行时 |
| `KoiCardTexTool-lite.exe`（约 1 MB） | 需要装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0) |

> 首次运行会生成 `plan_maintex.json`、`preset\`、`ui-lang.txt` 等文件，放在 exe 旁边（绿色版，不写注册表）。

### 用法（图形界面）

1. 把卡片（`.png`）或文件夹**拖到窗口里**，或点「浏览…」
2. 选输出目录（默认是 `原目录\compressed`）
3. 按需调整贴图类型的「处理方式」和「最大边」；不确定就用预设
4. 点 **▶ 开始压缩**

> [!TIP]
> **关于「独占贴图保护」**：大部分情况下**可以关掉**。只有在**单张贴图在单件服装上的面积占比过大**时（例如连体紧身衣）才有必要打开 —— 那种情况下这张图是那个部位唯一的高清来源，缩了会明显发糊。其它时候可以考虑关掉（体积更小、速度更快）。

### 用法（命令行）

```bat
KoiCardTexTool.exe scan     card.png                     :: 看卡片里有哪些贴图
KoiCardTexTool.exe parts    card.png                     :: 列出每个部位的贴图
KoiCardTexTool.exe compress in.png out.png 1024          :: 压一张（最大边 1024）
KoiCardTexTool.exe compress in.png out.png 0             :: 只做无损再压缩（不缩放）
KoiCardTexTool.exe batch    D:\cards D:\out 1024 auto 90 :: 批量
KoiCardTexTool.exe batch    D:\cards preset=my.json      :: 套用预设批量
KoiCardTexTool.exe help                                  :: 完整参数
```

### 构建 / 自检

```powershell
pwsh -File tools/build.ps1        # 产出 dist\self-contained 与 dist\lite
pwsh -File tools/selftest.ps1 -Card "你的服装卡.png"
```

需要 .NET 8 SDK。**卡要你自己提供** —— 仓库里不含任何游戏素材或他人的卡片。

### 常见问题

- **压完变大？** 工具只在"确实更小"时才替换，所以不会变大；如果日志显示"原样保留"，说明这张图重压不划算。
- **法线/掩罩能缩吗？** 能，它们是体积大头。缩到 1024 通常肉眼无感（游戏里本来就模糊）。
- **「放大 ×2 / ×4」很贵**：实测全彩色类 ×2 会让整卡 **+36%**，默认收起。
- **人物卡和服装卡不是一回事**：服装卡里只有贴图；人物卡里还有角色数据。

### 免责声明

本工具是**非官方**的第三方工具，与游戏厂商无任何关联。仓库内**不含**任何游戏素材、模型或他人卡片。使用者需自行拥有游戏，修改卡片的风险自负。

### 许可

[MIT](LICENSE)。其中 JPEG 最优哈夫曼表的实现参考了 **libjpeg**（IJG 许可）的算法，见 [NOTICE](NOTICE)。

---

## English

### What it is

Recompresses the textures inside Koikatsu outfit / character cards, shrinking cards dramatically (often −50%~80%) with minimal quality loss. GUI included; CLI available.

**Three modes** — whole card or folder in bulk · per-part fine control for outfit cards · per-group fine control for chara cards.

**Highlights**

- Main texture and the rest are configured separately (normals/masks rarely need full resolution)
- **Lossless options**: PNG recompression, optimal JPEG Huffman tables, grayscale→2 channels (pixel-identical)
- **Exclusive-texture protection**: textures used by a single part are not downscaled
- **Presets**: tune one card, apply the settings to other cards of the same outfit
- Four UI languages (中文 / English / 日本語 / 한국어), light & dark themes
- Handles HDR reflection maps, 8192px textures and alpha masks

### Download

Grab a build from [Releases](../../releases): the **self-contained** exe (≈66 MB, nothing to install) or the **lite** exe (≈1 MB, needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)).

### Usage

GUI: drop a card (`.png`) or a folder into the window, pick an output folder, adjust the per-type settings (or just pick a preset) and press **Start**.

> [!TIP]
> **About "Protect exclusive"**: you can turn it **off in most cases**. It is only needed when a **single texture covers a large share of one outfit** (a bodysuit, for example) — there that texture is the part's only hi-res source and downscaling it shows. Otherwise consider leaving it off (smaller cards, faster runs).

CLI:

```bat
KoiCardTexTool.exe scan     card.png
KoiCardTexTool.exe compress in.png out.png 1024      :: max edge 1024
KoiCardTexTool.exe compress in.png out.png 0         :: lossless only (no resizing)
KoiCardTexTool.exe batch    D:\cards D:\out 1024 auto 90
KoiCardTexTool.exe help
```

### Build

```powershell
pwsh -File tools/build.ps1
pwsh -File tools/selftest.ps1 -Card "your-outfit-card.png"
```

Requires the .NET 8 SDK. **Bring your own cards** — this repository ships no game assets.

### Disclaimer

Unofficial third-party tool, not affiliated with the game's developers or publishers. No game assets, models or third-party cards are included. You need to own the game; use at your own risk.

### License

[MIT](LICENSE). The optimal JPEG Huffman implementation follows **libjpeg**'s algorithm (IJG license) — see [NOTICE](NOTICE).

---

## 日本語

### これは何か

コイカツの衣装カード・キャラカード内のテクスチャを**再圧縮**して、カードを大幅に小さくするツールです（多くの場合 −50%〜80%）。GUI と CLI の両方があります。

**3 つのモード**：カード／フォルダ一括 · 衣装カードの部位ごと詳細設定 · キャラカードの「組・部位」ごと詳細設定。

**特長**

- メインテクスチャとその他を別々に設定（法線・マスクは高解像度が不要なことが多い）
- **無劣化オプション**：PNG 再圧縮、JPEG 最適ハフマン、グレースケールを 2 チャンネル化（画素は完全に不変）
- **専有テクスチャ保護**：1 部位だけが使うテクスチャは縮小しない
- **プリセット**：1 枚調整して同じ衣装の他カードに適用
- 4 言語 UI（中文 / English / 日本語 / 한국어）、ライト／ダークテーマ
- HDR 反射マップ、8192px テクスチャ、アルファマスクに対応

### ダウンロード

[Releases](../../releases) から。**自己完結版**（約 66 MB、インストール不要）か **lite 版**（約 1 MB、[.NET 8 デスクトップランタイム](https://dotnet.microsoft.com/download/dotnet/8.0)が必要）。

### 使い方

GUI：カード（`.png`）またはフォルダをウィンドウにドロップ → 出力先を選ぶ → 必要なら種類ごとの設定を調整（プリセットでも可）→ **開始**。

CLI は `scan` / `parts` / `compress` / `batch`、詳細は `help`。

> [!TIP]
> **「専有テクスチャを保護」について**：多くの場合**オフにできます**。**1 枚のテクスチャが 1 着の中で占める面積が大きい**とき（例：ボディスーツ）だけ必要です —— その場合はその部位唯一の高解像度元なので、縮めると目立ちます。それ以外はオフを検討してください（容量が小さく、速くなります）。

### ビルド

```powershell
pwsh -File tools/build.ps1
pwsh -File tools/selftest.ps1 -Card "衣装カード.png"
```

.NET 8 SDK が必要。**カードはご自身で用意してください** ——本リポジトリにゲーム素材は含まれません。

### 免責

非公式のサードパーティ製ツールであり、ゲームの開発・販売元とは無関係です。ゲーム素材・モデル・他人のカードは一切含まれていません。ゲーム本体を各自でご用意ください。使用は自己責任で。

### ライセンス

[MIT](LICENSE)。JPEG 最適ハフマン表の実装は **libjpeg**（IJG ライセンス）のアルゴリズムに基づきます（[NOTICE](NOTICE) 参照）。

---

## 한국어

### 무엇인가

코이카츠 의상 카드 / 캐릭터 카드 안의 텍스처를 **재압축**해 카드 용량을 크게 줄이는 도구입니다(보통 −50%~80%). GUI와 CLI를 모두 제공합니다.

**세 가지 모드**: 카드/폴더 일괄 · 의상 카드 부위별 세부 설정 · 캐릭터 카드 「그룹·부위」별 세부 설정.

**특징**

- 메인 텍스처와 나머지를 따로 설정 (노멀·마스크는 고해상도가 필요 없는 경우가 많음)
- **무손실 옵션**: PNG 재압축, JPEG 최적 허프만, 그레이스케일 2채널화 (화소 완전 동일)
- **전용 텍스처 보호**: 한 부위만 쓰는 텍스처는 축소하지 않음
- **프리셋**: 한 장을 조정해 같은 의상의 다른 카드에 적용
- 4개 언어 UI(中文 / English / 日本語 / 한국어), 라이트·다크 테마
- HDR 반사 맵, 8192px 텍스처, 알파 마스크 지원

### 다운로드

[Releases](../../releases)에서 받으세요. **자체 포함판**(약 66 MB, 설치 불필요) 또는 **lite판**(약 1 MB, [.NET 8 데스크톱 런타임](https://dotnet.microsoft.com/download/dotnet/8.0) 필요).

### 사용법

GUI: 카드(`.png`)나 폴더를 창에 드롭 → 출력 폴더 선택 → 필요하면 종류별 설정 조정(프리셋 가능) → **시작**.

CLI: `scan` / `parts` / `compress` / `batch`, 자세한 내용은 `help`.

> [!TIP]
> **「전용 텍스처 보호」에 대하여**：대부분의 경우 **꺼도 됩니다**. **한 장의 텍스처가 한 벌에서 차지하는 면적이 클 때**(예: 전신 타이즈)만 필요합니다 —— 그때는 그 부위의 유일한 고해상도 원본이라 줄이면 티가 납니다. 그 외에는 끄는 것을 고려하세요 (용량이 작아지고 빨라집니다).

### 빌드

```powershell
pwsh -File tools/build.ps1
pwsh -File tools/selftest.ps1 -Card "의상카드.png"
```

.NET 8 SDK가 필요합니다. **카드는 직접 준비하세요** —— 이 저장소에는 게임 리소스가 포함되지 않습니다.

### 면책

비공식 서드파티 도구이며 게임 개발사·배급사와 무관합니다. 게임 리소스·모델·타인의 카드는 포함되지 않습니다. 게임 본편은 각자 준비하세요. 사용은 자기 책임입니다.

### 라이선스

[MIT](LICENSE). JPEG 최적 허프만 구현은 **libjpeg**(IJG 라이선스) 알고리즘을 따릅니다 ([NOTICE](NOTICE) 참조).
