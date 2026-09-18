# 构建脚本：产出两个版本
#
#   self-contained  —— 自包含单文件，别人不用装 .NET 也能跑（约 66 MB）
#   lite            —— 轻量版，需要装 .NET 8 桌面运行时（约 1 MB）
#
# 用法（在仓库根目录或任意位置都行）：
#   pwsh -File tools/build.ps1
#   pwsh -File tools/build.ps1 -OutRoot D:\out
#
# 路径全部相对脚本自身，不依赖任何本机绝对路径。

param(
    [string]$Configuration = 'Release',
    [string]$OutRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $repo 'src\KoiCardTexTool\KoiCardTexTool.csproj'

if (-not (Test-Path $proj)) { throw "找不到工程文件：$proj" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw ' 没找到 dotnet，请先安装 .NET 8 SDK' }

$ver = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "== 构建 KoiCardTexTool $ver ($Configuration / $Runtime) ==" -ForegroundColor Cyan

$sc = Join-Path $OutRoot 'self-contained'
$lite = Join-Path $OutRoot 'lite'

Write-Host '-- 自包含单文件版 --' -ForegroundColor Yellow
# EnableCompressionInSingleFile 很关键：不压的话单文件版会大出一倍多（145 MB vs 66 MB）
& dotnet publish $proj -c $Configuration -r $Runtime --self-contained true `
    -p:PublishTrimmed=false -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $sc
if ($LASTEXITCODE -ne 0) { throw "自包含版构建失败（退出码 $LASTEXITCODE）" }

Write-Host '-- 轻量版（框架依赖）--' -ForegroundColor Yellow
& dotnet publish $proj -c $Configuration --self-contained false `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -p:_RequiresILLinkPack=false `
    -o $lite
if ($LASTEXITCODE -ne 0) { throw "轻量版构建失败（退出码 $LASTEXITCODE）" }

foreach ($d in @($sc, $lite)) {
    $exe = Join-Path $d 'KoiCardTexTool.exe'
    if (-not (Test-Path $exe)) { throw "构建完成但没找到产物：$exe" }
    $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host ("   {0}  ({1} MB)" -f $exe, $mb) -ForegroundColor Green
}

Write-Host ''
Write-Host '完成。自检：pwsh -File tools/selftest.ps1 -Card <你的服装卡.png>' -ForegroundColor Cyan
