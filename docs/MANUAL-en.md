# Manual (1.0)

Koikatsu Coordinate/Chara Texture Optimization Tool — recompresses the textures inside a card to make the card file smaller.

---

## 1. Quick start

1. Drag a card (`.png`) or a **whole folder** into the window (you can also drop it on the EXE icon, or click **Browse…**)
2. Pick an output folder (default: `<card folder>\compressed`)
3. Choose a **Preset** — if unsure, pick **Quality**
4. Click **▶ Start**

Progress and results appear in the **Log** panel on the right. The compressed cards are written to the output folder; **your originals are never modified**.

> Portable: everything is stored next to the EXE (`preset\`, `ui-lang.txt`, `ui-theme.txt`, `ui-layout.json`).
> Nothing is written to the registry or system folders — deleting the folder uninstalls it.

---

## 2. Which of the three modes?

Three tabs at the top:

| Tab | When to use | Notes |
|---|---|---|
| **Batch (cards / folder)** | One card, a folder, or many cards at once | Configured per **texture type** (MainTex / Normal / Mask / Height…). The usual choice |
| **Outfit card (per-part)** | You want a specific part of one outfit to stay sharp | Configured per **part** (top / bot / bra / accessories…) |
| **Chara card** | Character cards (`Koikatu_F_*.png`) | Chara cards have a *Group · Part* structure; unify per group or set per part |

**Chara cards and outfit cards are not the same thing.** An outfit card contains only textures; a chara card also contains character data (face, body, personality…), so use tab 3 for it — don't apply outfit settings from tab 1.

---

## 3. Options (tab 1)

### Texture type table

Each row is a texture type with two dropdowns:

| Column | Meaning | Suggested |
|---|---|---|
| **Format** | `Auto (JPEG only when safe and smaller)` = convert to JPEG only if that texture is actually suitable and the result is smaller; `PNG` / `JPEG` = force it; `Keep` = don't touch | Leave on **Auto** |
| **Max edge** | Downscale so the longest edge fits (`4096 / 2048 / 1024 / 512`); `No resize` keeps the resolution | MainTex 2048, Normal/Mask 1024 is a common combo |
| **Scan result** | Texture count, size and a verdict for this type | It tells you whether JPEG is safe for that kind of map |

> Normals, masks and height maps **rarely need full resolution** — and they are usually where most of the bytes are, so downscaling them pays off the most.

### Unify

**Unified setting** + **Apply to all types** writes one set of values to every type at once. You can still fine-tune individual types afterwards.

### Switches below

| Option | Meaning |
|---|---|
| **JPEG quality** | 90 by default. Lower = smaller and blurrier. Only affects textures converted to JPEG |
| **Sharpen** | Sharpens after downscaling to recover detail lost to area averaging. 0–200%, or type a value up to 500%. Only applies to textures that were actually downscaled |
| **Sharpen mode** | `Contrast adaptive` (default: gentler near strong edges, fewer halos) / `Edge aware` (sharper edges, more halos) |
| **Grayscale 2ch** | A grayscale image (all three channels equal) is stored as one channel: **pixels unchanged**, smaller file |
| **Preserve detail maps** | Uses area averaging when downscaling (clearer). Unchecked = plain resize (faster) |
| **Area-average colors** | Applies area averaging + edge sharpening to colour textures too |
| **Protect exclusive** | Textures used by only one part are not downscaled — they are often that part's only hi-res source. The **Up to** box decides how far to relax them |
| **Upscale** | ×2 / ×4 upscales textures below *Max edge*. **Expensive** (measured: a card can grow by ~36%). Hidden by default; most people don't need it |

---

## 4. Presets

A preset bundles *part rules + per-texture rules + general settings* into one JSON file so you can apply it to **other cards of the same outfit**.

| Built-in preset | For |
|---|---|
| **Optimize only** | Lossless recompression only — **zero quality loss**, modest savings |
| **Performance** | Size first, quality still fine |
| **Performance (aggressive)** | Smallest possible |
| **Quality** | Quality first (**pick this if unsure**) |

- **Save as preset…** exports your tuned settings to a JSON file
- **Load preset…** applies a file someone shared with you
- **Open preset folder** shows what the built-ins look like
- **Set as the default preset** applies it automatically on every start

When compressing another card the tool first computes a *same-outfit match score*: **below the threshold the card is skipped** (the log prints the score). Add `force=1` to compress it anyway using only the general settings.

---

## 5. Command line

```bat
KoiCardTexTool.exe help                                   :: full reference

KoiCardTexTool.exe scan     card.png                      :: list the textures in a card
KoiCardTexTool.exe parts    card.png                      :: list textures per part

KoiCardTexTool.exe compress in.png out.png 1024           :: one card, max edge 1024
KoiCardTexTool.exe compress in.png out.png 0              :: lossless only (no resizing)
KoiCardTexTool.exe compress in.png out.png 1024 jpg 90    :: jpg, quality 90
KoiCardTexTool.exe compress in.png out.png 1024 maintex   :: compress MainTex only, leave the rest untouched

KoiCardTexTool.exe batch    D:\cards D:\out 1024 auto 90  :: batch
KoiCardTexTool.exe batch    D:\cards preset=my.json       :: batch with a preset
KoiCardTexTool.exe batch    D:\cards preset=my.json force=1   :: also cards of other outfits (general settings only)

KoiCardTexTool.exe preset   card.png a.json parts=top:auto:512 tex=26:png:256
```

`mode` = `png` / `jpg` / `auto` (default) / `maintex` (MainTex only)
`max_size` = longest-edge limit, `0` = no resizing

---

## 6. FAQ

**Did it get bigger?**
No. A texture is replaced only when the result is actually smaller; "kept as is" in the log means recompressing that one wasn't worth it.

**Can normals and masks be downscaled?**
Yes — and it's usually the biggest win. They often make up more than half the file, and 1024 is hard to notice in game.

**Will the game look wrong after downscaling?**
Lower resolution affects close-up detail, not material bindings. **Back up your cards** before compressing.

**HDR environment / reflection maps?**
Supported (Radiance HDR). They follow the rules like everything else, or set them to *Keep* to leave them completely untouched.

**Why is Upscale hidden by default?**
Measured: upscaling all colour types ×2 grows a card by **~36%** (0%–142% per card). Most people only need MainTex upscaled.

**What does "textures shared by parts" mean in the log?**
One texture can be used by several parts. It is only modified if **all** of those parts are included in your settings.

**Can I change the UI language / theme?**
Yes — `Language` (中文 / English / 日本語 / 한국어) and `Theme` (Light / Dark) at the top right; your choice is remembered.

**Where do the output files go?**
By default `<card folder>\compressed\`. Names get a `[zip]` suffix by default; you can enable *Overwrite existing* or *Include subfolders*.

---

## 7. Disclaimer

Unofficial third-party tool, not affiliated with the game's developers or publishers.
You need to own the game. **Always back up your cards** — use at your own risk.
