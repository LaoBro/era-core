<#
.SYNOPSIS
EraCore 发布脚本——包办 CLI / MAUI(Windows、Android) 及 NativeAOT 变体的发布，
统一把最终可分发产物（exe/apk，而非 dll）收集到仓库根 artifacts/ 下，完成后打印路径。

.DESCRIPTION
用法（PowerShell）：
  .\scripts\publish.ps1                  # 显示用法
  .\scripts\publish.ps1 cli              # CLI 普通发布（框架依赖单文件）
  .\scripts\publish.ps1 cli-aot          # CLI NativeAOT win-x64（自动探测 MSVC，或 -MsvcLib）
  .\scripts\publish.ps1 maui-win         # MAUI Windows 普通发布
  .\scripts\publish.ps1 maui-win-aot     # MAUI Windows NativeAOT（首次尝试，可能失败）
  .\scripts\publish.ps1 maui-android     # MAUI Android 普通 Release（默认 arm64）
  .\scripts\publish.ps1 maui-android-aot # MAUI Android NativeAOT（默认 arm64）
  .\scripts\publish.ps1 all              # 全部（cli + cli-aot + maui-win + maui-win-aot + maui-android + maui-android-aot）
  .\scripts\publish.ps1 list             # 列出 artifacts/ 下已有产物（无参数默认此行为）

参数：
  -Rid arm64|x64|all   Android RID 短名（默认 arm64；实际传 android-arm64/android-x64，Rid=all 时两者都发布）
  -Rebuild            全量重建兜底：清 artifacts 输出 + AOT 清 obj-aot/bin-aot + 强制前端重建
  -SkipFrontend       强制跳过前端重建（即使源码有更新也不重建）
  -Clean              发布前清空对应 artifacts 子目录（-Rebuild 已包含此行为）
  -MsvcLib <path>      cli-aot 手动指定 MSVC LIB（分号分隔三段），不传则自动探测

前端策略（MAUI 目标）：默认快路径——仅当前端源码比 wwwroot 产物新（或 -Rebuild）时
才 npm build + 清空并重拷 wwwroot；源码未变化时直接复用现有 wwwroot，避免每次都重建。
清空 wwwroot 只发生在重建路径（load-time glob：MauiAsset 在项目加载时评估，必须预先
置好 wwwroot，见 docs/LESSONS/testing-and-ci.md #11）。产物异常（启动闪退 / 前端不生效）
时用 -Rebuild 强制全量重建。
#>
[CmdletBinding()]
param(
  [Parameter(Position = 0)]
  [string]$Target = '',
  [ValidateSet('arm64', 'x64', 'all')]
  [string]$Rid = 'arm64',
  [string]$MsvcLib = '',
  [switch]$SkipFrontend,
  [switch]$Clean,
  [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Artifacts = Join-Path $RepoRoot 'artifacts'
$script:FrontendBuilt = $false

# ============ 工具函数 ============

function Show-Usage {
  @'
用法：.\scripts\publish.ps1 <目标> [选项]

目标：
  cli               CLI 普通发布（框架依赖单文件）→ artifacts/cli/
  cli-aot           CLI NativeAOT win-x64 → artifacts/cli-aot/win-x64/
  maui-win          MAUI Windows 普通发布 → artifacts/maui-win/
  maui-win-aot      MAUI Windows NativeAOT → artifacts/maui-win-aot/win-x64/（首次尝试，可能失败）
  maui-android      MAUI Android 普通 Release → artifacts/maui-android/<rid>/
  maui-android-aot  MAUI Android NativeAOT → artifacts/maui-android-aot/<rid>/
  all               以上全部
  list              列出 artifacts/ 下已有产物（无参数默认此行为）

选项：
  -Rid arm64|x64|all   Android RID 短名（默认 arm64；实际传 android-arm64/android-x64，Rid=all 时两者都发布）
  -Rebuild            全量重建兜底：清 artifacts 输出 + AOT 清 obj-aot/bin-aot + 强制前端重建（产物异常时用）
  -SkipFrontend       强制跳过前端重建（即使源码有更新也不重建）
  -Clean              仅清空对应 artifacts 子目录（-Rebuild 已包含此行为）
  -MsvcLib <path>      cli-aot 手动指定 MSVC LIB（分号分隔三段），不传则自动探测
'@
}

function Invoke-Dotnet {
  param([string[]]$DotnetArgs)
  Write-Host "  dotnet $($DotnetArgs -join ' ')" -ForegroundColor DarkGray
  dotnet @DotnetArgs
  if ($LASTEXITCODE -ne 0) {
    throw "dotnet 失败（exit=$LASTEXITCODE）: dotnet $($DotnetArgs -join ' ')"
  }
}

function Reset-Dir {
  param([string]$Path)
  if (($Clean -or $Rebuild) -and (Test-Path $Path)) {
    Remove-Item $Path -Recurse -Force
  }
  New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Find-Artifact {
  param([string]$Dir, [string]$Name)
  Get-ChildItem $Dir -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq $Name } |
    Select-Object -First 1
}

function Show-Artifact {
  param([string]$Label, [System.IO.FileInfo]$Artifact)
  if (-not $Artifact) {
    Write-Host "  [!] $Label 产物未找到（检查上面的构建输出）" -ForegroundColor Yellow
    return
  }
  $size = '{0:N1} MB' -f ($Artifact.Length / 1MB)
  Write-Host "  [OK] $Label" -ForegroundColor Green
  Write-Host "       $($Artifact.FullName)" -ForegroundColor Green
  Write-Host "       ($size)" -ForegroundColor DarkGray
}

# ============ 前端构建（MAUI 目标共用，整个调用只跑一次） ============
# 策略：默认快路径——仅当前端源码比 wwwroot 产物新（或 -Rebuild）时才 npm build +
# 清空并重拷 wwwroot；源码未变化时直接复用现有 wwwroot。清空 wwwroot 只发生在这里
# （load-time glob：MauiAsset 在项目加载时评估，必须先于 dotnet publish 预置好
# wwwroot，见 docs/LESSONS/testing-and-ci.md #11）。

function Test-FrontendStale {
  # true = 前端源码比 wwwroot 产物新（或产物缺失），需要重建
  $web = Join-Path $RepoRoot 'EraCore.Web'
  $wwwroot = Join-Path $RepoRoot 'EraCore.Maui\wwwroot'
  $prod = Join-Path $wwwroot 'index.html'
  if (-not (Test-Path $prod)) { return $true }
  $srcNewest = Get-ChildItem (Join-Path $web 'src') -Recurse -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if (-not $srcNewest) { return $false }
  return $srcNewest.LastWriteTime -gt (Get-Item $prod).LastWriteTime
}

function Invoke-FrontendBuild {
  if ($script:FrontendBuilt) { return }
  $web = Join-Path $RepoRoot 'EraCore.Web'
  $dist = Join-Path $web 'dist-maui'
  $wwwroot = Join-Path $RepoRoot 'EraCore.Maui\wwwroot'

  $reason = ''
  if ($Rebuild) { $reason = '强制全量重建（-Rebuild）' }
  elseif (-not $SkipFrontend -and (Test-FrontendStale)) { $reason = '前端源码比产物新，自动重建' }

  if (-not $reason) {
    $script:FrontendState = if ($SkipFrontend) { '跳过（-SkipFrontend）' } else { '复用现有 wwwroot（源码未变化）' }
    Write-Host "  前端：$($script:FrontendState)" -ForegroundColor DarkGray
    return
  }

  Write-Host "`n== 前端重建（$reason）==" -ForegroundColor Cyan
  Push-Location $web
  try {
    npm run build -- --base=./ --outDir=dist-maui
    if ($LASTEXITCODE -ne 0) { throw 'npm run build 失败' }
  }
  finally { Pop-Location }
  New-Item -ItemType Directory -Force -Path $wwwroot | Out-Null
  Get-ChildItem $wwwroot -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
  Copy-Item (Join-Path $dist '*') $wwwroot -Recurse -Force
  $script:FrontendBuilt = $true
  $script:FrontendState = '已重建并拷贝至 wwwroot'
  Write-Host "  前端已重建并拷贝至 wwwroot" -ForegroundColor Green
}

# ============ AOT 中间产物（增量/清理 + 提示） ============
# Android NativeAOT 的 _AddStaticResources 增量 flag 不认运行时形态切换——换形态复用
# obj-aot 可能漏生成 NativeAotRuntimeProvider.java → dex 缺类 → 启动即闪退。默认保留
# 增量（快）；-Rebuild 时清 obj-aot/bin-aot 强制全量（见 docs/LESSONS/nativeaot.md #9）。

function Assert-AotState {
  param([string]$Note)
  if ($Rebuild) {
    foreach ($d in @((Join-Path $RepoRoot 'EraCore.Maui\obj-aot'), (Join-Path $RepoRoot 'EraCore.Maui\bin-aot'))) {
      if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    }
    Write-Host "  AOT 中间产物已清理（-Rebuild）" -ForegroundColor DarkGray
  } else {
    Write-Host "  AOT 中间产物：增量（未清 obj-aot）$Note" -ForegroundColor DarkGray
  }
}

# ============ CLI 目标 ============

function Publish-Cli {
  Write-Host "`n=== CLI 普通发布（框架依赖单文件）===" -ForegroundColor Cyan
  $out = Join-Path $Artifacts 'cli'
  Reset-Dir $out
  Invoke-Dotnet @(
    'publish', (Join-Path $RepoRoot 'EraCore.Cli\EraCore.Cli.csproj'),
    '-c', 'Release', '--no-self-contained', '-o', $out
  )
  Show-Artifact 'CLI 可执行文件' (Find-Artifact $out 'EraCore.Cli.exe')
}

function Publish-CliAot {
  Write-Host "`n=== CLI NativeAOT win-x64 ===" -ForegroundColor Cyan
  $out = Join-Path $Artifacts 'cli-aot\win-x64'
  Reset-Dir $out
  $msvc = Resolve-MsvcEnv -ExplicitLib $MsvcLib
  $env:LIB = $msvc.Lib
  try {
    $args = @(
      'publish', (Join-Path $RepoRoot 'EraCore.Cli\EraCore.Cli.csproj'),
      '-c', 'Release', '-r', 'win-x64',
      '-p:PublishAot=true', '-p:IlcUseEnvironmentalTools=true',
      '-p:TreatWarningsAsErrors=false',   # 继承 nativeaot-verify-report 可复现命令：ILC 分析警告降级放行
      '-o', $out
    )
    # MSVC 链接器自动定位不稳定——显式指 link.exe/lib.exe（vswhere 探测到才传）
    if ($msvc.Linker) { $args += "-p:CppLinker=$($msvc.Linker)" }
    if ($msvc.LibCreator) { $args += "-p:CppLibCreator=$($msvc.LibCreator)" }
    Invoke-Dotnet $args
  }
  finally { Remove-Item Env:LIB -ErrorAction SilentlyContinue }
  Show-Artifact 'CLI NativeAOT 单文件 exe' (Find-Artifact $out 'EraCore.Cli.exe')
}

# ============ MAUI Windows 目标 ============

function Publish-MauiWin {
  Write-Host "`n=== MAUI Windows 普通发布 ===" -ForegroundColor Cyan
  Invoke-FrontendBuild
  $out = Join-Path $Artifacts 'maui-win'
  Reset-Dir $out
  Invoke-Dotnet @(
    'publish', (Join-Path $RepoRoot 'EraCore.Maui\EraCore.Maui.csproj'),
    '-f', 'net10.0-windows10.0.19041.0', '-c', 'Release',
    "-p:PublishDir=$out\", '-p:SkipVueBuild=true'
  )
  Show-Artifact 'MAUI Windows 可执行文件' (Find-Artifact $out 'EraCore.Maui.exe')
}

function Publish-MauiWinAot {
  Write-Host "`n=== MAUI Windows NativeAOT（首次尝试，可能失败）===" -ForegroundColor Cyan
  Invoke-FrontendBuild
  Assert-AotState -Note '——如遇构建异常，加 -Rebuild 全量重建'
  $out = Join-Path $Artifacts 'maui-win-aot\win-x64'
  Reset-Dir $out
  Invoke-Dotnet @(
    'publish', (Join-Path $RepoRoot 'EraCore.Maui\EraCore.Maui.csproj'),
    '-f', 'net10.0-windows10.0.19041.0', '-c', 'Release',
    '-r', 'win-x64', '--self-contained', '-p:PublishAot=true',
    "-p:PublishDir=$out\", '-p:SkipVueBuild=true'
  )
  Show-Artifact 'MAUI Windows NativeAOT exe' (Find-Artifact $out 'EraCore.Maui.exe')
}

# ============ MAUI Android 目标 ============

function Publish-MauiAndroid {
  param([string]$RidValue)
  Write-Host "`n=== MAUI Android 普通 Release（RID=android-$RidValue）===" -ForegroundColor Cyan
  Invoke-FrontendBuild
  $out = Join-Path $Artifacts ("maui-android\{0}" -f $RidValue)
  Reset-Dir $out
  Invoke-Dotnet @(
    'publish', (Join-Path $RepoRoot 'EraCore.Maui\EraCore.Maui.csproj'),
    '-f', 'net10.0-android', '-r', "android-$RidValue", '-c', 'Release',
    "-p:PublishDir=$out\", '-p:SkipVueBuild=true'
  )
  Show-Artifact "MAUI Android APK（android-$RidValue）" (Find-Artifact $out 'com.eracore.maui-Signed.apk')
}

function Publish-MauiAndroidAot {
  param([string]$RidValue)
  Write-Host "`n=== MAUI Android NativeAOT（RID=android-$RidValue）===" -ForegroundColor Cyan
  Invoke-FrontendBuild
  Assert-AotState -Note '——如遇启动即闪退（dex 缺 NativeAotRuntimeProvider），加 -Rebuild 全量重建'
  $out = Join-Path $Artifacts ("maui-android-aot\{0}" -f $RidValue)
  Reset-Dir $out
  Invoke-Dotnet @(
    'publish', (Join-Path $RepoRoot 'EraCore.Maui\EraCore.Maui.csproj'),
    '-f', 'net10.0-android', '-r', "android-$RidValue", '-c', 'Release',
    '-p:PublishAot=true', '-p:RunAOTCompilation=false', '-p:AndroidPackageFormats=apk',
    "-p:PublishDir=$out\", '-p:SkipVueBuild=true'
  )
  Show-Artifact "MAUI Android NativeAOT APK（android-$RidValue）" (Find-Artifact $out 'com.eracore.maui-Signed.apk')
}

# ============ MSVC 探测（cli-aot） ============

function Resolve-MsvcEnv {
  param([string]$ExplicitLib)
  if ($ExplicitLib) {
    Write-Host "  使用 -MsvcLib 指定的 LIB" -ForegroundColor DarkGray
    return @{ Lib = $ExplicitLib; Linker = ''; LibCreator = '' }
  }
  $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
  if (-not (Test-Path $vswhere)) {
    throw "未找到 vswhere（$vswhere）。请安装 VS「使用 C++ 的桌面开发」工作负载，或用 -MsvcLib 手动指定。"
  }
  $vsPath = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath |
    Select-Object -First 1)
  if (-not $vsPath) {
    throw 'vswhere 未定位到含 VC 工具的 VS 安装。请用 -MsvcLib 手动指定。'
  }
  $msvc = Get-ChildItem (Join-Path $vsPath 'VC\Tools\MSVC') -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
  if (-not $msvc) { throw "未在 $vsPath 找到 MSVC 工具集。请用 -MsvcLib 手动指定。" }
  $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Lib'
  $sdk = Get-ChildItem $sdkRoot -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
  if (-not $sdk) { throw "未在 $sdkRoot 找到 Windows SDK。请用 -MsvcLib 手动指定。" }
  $lib = "$($msvc.FullName)\lib\onecore\x64;$($sdk.FullName)\um\x64;$($sdk.FullName)\ucrt\x64"
  $bin = Join-Path $msvc.FullName 'bin\Hostx64\x64'
  Write-Host "  自动探测 MSVC：$lib" -ForegroundColor DarkGray
  return @{
    Lib        = $lib
    Linker     = Join-Path $bin 'link.exe'
    LibCreator = Join-Path $bin 'lib.exe'
  }
}

# ============ 产物列表 ============

function Show-List {
  Write-Host "`n=== artifacts/ 现有产物 ===" -ForegroundColor Cyan
  if (-not (Test-Path $Artifacts)) { Write-Host '  （空）'; return }
  $found = $false
  Get-ChildItem $Artifacts -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '\.(exe|apk)$' } |
    ForEach-Object { $found = $true; Write-Host "  $($_.FullName)" -ForegroundColor Green }
  if (-not $found) { Write-Host '  （空）' }
}

# ============ 主流程 ============

if (-not $Target) { Show-Usage; exit 0 }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
  throw '未找到 dotnet——请先安装 .NET SDK'
}

switch ($Target) {
  'cli' { Publish-Cli }
  'cli-aot' { Publish-CliAot }
  'maui-win' { Publish-MauiWin }
  'maui-win-aot' { Publish-MauiWinAot }
  'maui-android' {
    if ($Rid -eq 'all') { Publish-MauiAndroid 'arm64'; Publish-MauiAndroid 'x64' } else { Publish-MauiAndroid $Rid }
  }
  'maui-android-aot' {
    if ($Rid -eq 'all') { Publish-MauiAndroidAot 'arm64'; Publish-MauiAndroidAot 'x64' } else { Publish-MauiAndroidAot $Rid }
  }
  'all' {
    Publish-Cli
    Publish-CliAot
    Publish-MauiWin
    Publish-MauiWinAot
    Publish-MauiAndroid 'arm64'
    Publish-MauiAndroidAot 'arm64'
  }
  'list' { Show-List; exit 0 }
  default { Show-Usage; exit 1 }
}

Write-Host "`n=== 全部完成，产物位于 $Artifacts ===" -ForegroundColor Green
