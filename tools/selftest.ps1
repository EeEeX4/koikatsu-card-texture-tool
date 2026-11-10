# 自检：构建 + 界面 + 端到端压缩 + 语言/主题
#
# 用法：
#   pwsh -File tools/selftest.ps1 -Card "D:\cards\Alice.png"
#   pwsh -File tools/selftest.ps1 -Card a.png -OutfitCard b.png -CharaCard c.png
#
# 说明：
#   · **卡要你自己提供** —— 仓库里不含任何游戏素材或别人的卡。
#   · -Card 是「任意一张卡」，跑第 1 页（批量压缩）与压缩流程自检；
#   · -OutfitCard 是「单件服装卡」，跑第 2 页；-CharaCard 是「人物卡」，跑第 3 页；
#     不传就跳过对应页面的自检（其余照样跑）。
#
# 判定标准与开发时一致：退出码 0、无异常、界面无文字被截断、压缩产物比原卡小。

param(
    [Parameter(Mandatory = $true)][string]$Card,
    [string]$OutfitCard,
    [string]$CharaCard,
    [string]$Exe = '',
    [string]$Work = (Join-Path ([System.IO.Path]::GetTempPath()) ('kktex-selftest-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)

$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 默认按 OEM 代码页读取子进程输出；exe 的输出是 UTF-8，
# 不设这里中文会乱码（「单元格重绘」这类自检会误报 0 次）。
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
# Windows PowerShell 5.1 在参数默认值里拿不到 $PSScriptRoot，所以默认 Exe 放到这里解析。
if ([string]::IsNullOrEmpty($Exe)) {
    $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\self-contained\KoiCardTexTool.exe'
}
$fail = 0
$pass = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host ("  [PASS] {0}" -f $name) -ForegroundColor Green }
    else     { $script:fail++; Write-Host ("  [FAIL] {0}  {1}" -f $name, $detail) -ForegroundColor Red }
}

function Run-UiTest([string]$label, [string[]]$flags) {
    $args = @($Card, 'uitest') + $flags
    # 参数顺序：uitest 在前，卡路径可以是第一个
    $out = & $Exe uitest $Card @flags 2>&1 | Out-String
    $code = $LASTEXITCODE
    $bad = ($out -split "`n" | Where-Object { $_ -match 'UIEXC|DOMEXC|TASKEXC|Exception|未处理' }).Count
    $clip = ($out -split "`n" | Where-Object { $_ -match '\[切掉\]' }).Count
    Check "$label 退出码=0" ($code -eq 0) "退出码 $code"
    Check "$label 无异常" ($bad -eq 0) "$bad 行异常"
    Check "$label 界面无截断" ($clip -eq 0) "$clip 处被切"
    return $out
}

if (-not (Test-Path $Exe)) { throw "找不到程序：$Exe`n请先运行 pwsh -File tools/build.ps1" }
if (-not (Test-Path $Card)) { throw "找不到卡片：$Card" }

New-Item -ItemType Directory -Force -Path $Work | Out-Null
Write-Host "== 自检开始 ==" -ForegroundColor Cyan
Write-Host "程序：$Exe"
Write-Host "卡片：$Card"
Write-Host "临时目录：$Work"
Write-Host ''

# ---- 1. 结构读取 ----
Write-Host '[1/6] 读取卡片结构' -ForegroundColor Yellow
$scan = & $Exe scan $Card 2>&1 | Out-String
Check 'scan 退出码=0' ($LASTEXITCODE -eq 0) "退出码 $LASTEXITCODE"
Check 'scan 报告了贴图' ($scan -match 'textures\s*/') '输出里没有贴图统计'

# ---- 2. 界面自检（第 1 页 + 语言 + 主题） ----
Write-Host '[2/6] 界面自检（第 1 页）' -ForegroundColor Yellow
Run-UiTest '第1页' @() | Out-Null
Run-UiTest '第1页/英文' @('langforce=en') | Out-Null
Run-UiTest '第1页/日文' @('langforce=ja') | Out-Null
Run-UiTest '第1页/韩文' @('langforce=ko') | Out-Null
Run-UiTest '第1页/深色' @('theme=dark') | Out-Null

# ---- 3. 第 2/3 页（需要对应的卡） ----
Write-Host '[3/6] 界面自检（第 2/3 页）' -ForegroundColor Yellow
if ($OutfitCard -and (Test-Path $OutfitCard)) {
    $out2 = & $Exe uitest $OutfitCard tab2=1 2>&1 | Out-String
    Check '第2页 退出码=0' ($LASTEXITCODE -eq 0) "退出码 $LASTEXITCODE"
    Check '第2页 无异常' (($out2 -split "`n" | Where-Object { $_ -match 'UIEXC|Exception' }).Count -eq 0) '有异常'
    # 切语言后表头/单元格必须跟着变
    $sw = & $Exe uitest $OutfitCard tab2=1 langforce=zh lang=en,ja,ko 2>&1 | Out-String
    $langs = ($sw -split "`n" | Where-Object { $_ -match '^LANG\s+:' })
    Check '第2页 三种语言都切成功' ($langs.Count -eq 3) "只切了 $($langs.Count) 次"
    Check '第2页 切语言后单元格会重绘' (($langs | Where-Object { $_ -match '单元格重绘=([1-9][0-9]*)' }).Count -eq 3) '有语言切换后单元格没有重绘'
} else {
    Write-Host '  （未提供 -OutfitCard，跳过第 2 页）' -ForegroundColor DarkGray
}
if ($CharaCard -and (Test-Path $CharaCard)) {
    $out3 = & $Exe uitest $CharaCard tab3=1 2>&1 | Out-String
    Check '第3页 退出码=0' ($LASTEXITCODE -eq 0) "退出码 $LASTEXITCODE"
    Check '第3页 无异常' (($out3 -split "`n" | Where-Object { $_ -match 'UIEXC|Exception' }).Count -eq 0) '有异常'
} else {
    Write-Host '  （未提供 -CharaCard，跳过第 3 页）' -ForegroundColor DarkGray
}

# ---- 4. 端到端压缩 ----
#   compress <in> <out.png> [max_size] ...   ——  max_size=0 = 不缩放（只看无损再压缩的收益）
Write-Host '[4/6] 端到端压缩（max_size=0 = 不缩放）' -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$outFile = Join-Path $Work 'out.png'
$before = (Get-Item $Card).Length
& $Exe compress $Card $outFile 0 2>&1 | Out-Null
$code = $LASTEXITCODE
Check 'compress 退出码=0' ($code -eq 0) "退出码 $code"
$made = if (Test-Path $outFile) { Get-Item $outFile } else { $null }
Check '产出了文件' ($null -ne $made) "没有生成 $outFile"
if ($made) {
    Check '产物比原卡小' ($made.Length -lt $before) ("原 {0:N0} → 新 {1:N0}" -f $before, $made.Length)
    Write-Host ("      原 {0:N0} 字节 → 新 {1:N0} 字节（{2:P1}）" -f $before, $made.Length, ($made.Length / $before)) -ForegroundColor DarkGray
}

# ---- 5. 导出 / 删除贴图 ----
Write-Host '[5/6] 导出 / 删除贴图' -ForegroundColor Yellow
$texDir = Join-Path $Work 'textures'
& $Exe extract $Card $texDir 2>&1 | Out-Null
Check 'extract 退出码=0' ($LASTEXITCODE -eq 0) "退出码 $LASTEXITCODE"
$manifest = Join-Path $texDir 'manifest.csv'
Check 'extract 生成 manifest.csv' (Test-Path $manifest) "没有生成 $manifest"
$texExt = @('.png', '.jpg', '.jpeg', '.hdr', '.exr', '.bin')
$exported = @(Get-ChildItem -Recurse -File $texDir -ErrorAction SilentlyContinue |
              Where-Object { $texExt -contains $_.Extension.ToLower() })
Check 'extract 写出贴图' ($exported.Count -gt 0) '没有贴图文件'

# 从 parts 里取一个 TexID，在副本上删除它（绝不动原卡）
$partsOut = & $Exe parts $Card 2>&1 | Out-String
$m = [regex]::Match($partsOut, 'TexID\s+(\d+)')
if ($m.Success) {
    $tid = $m.Groups[1].Value
    $copy = Join-Path $Work 'del-in.png'
    Copy-Item $Card $copy -Force
    $delOut = Join-Path $Work 'del-out.png'
    & $Exe delete $copy $delOut "texids=$tid" 2>&1 | Out-Null
    Check 'delete 退出码=0' ($LASTEXITCODE -eq 0) "退出码 $LASTEXITCODE"
    Check 'delete 产出新卡' (Test-Path $delOut) "没有生成 $delOut"
    if (Test-Path $delOut) {
        Check '删除后卡片变小' ((Get-Item $delOut).Length -lt (Get-Item $copy).Length) '新卡没有变小'
        $after = & $Exe scan $delOut 2>&1 | Out-String
        Check '删除后仍能读取' ($LASTEXITCODE -eq 0 -and $after -match 'textures\s*/') '输出卡读取失败'
    }
} else {
    Check 'parts 输出里能找到 TexID' $false '没有匹配到 TexID'
}

# ---- 6. 结果 ----
Write-Host '[6/6] 汇总' -ForegroundColor Yellow
Write-Host ''
Write-Host ("通过 {0} 项，失败 {1} 项" -f $pass, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
Remove-Item -Recurse -Force $Work -ErrorAction SilentlyContinue
if ($fail -gt 0) { exit 1 }
Write-Host '全部通过。' -ForegroundColor Green
