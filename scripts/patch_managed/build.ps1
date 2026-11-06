param(
    [Parameter(Mandatory = $true)][string]$Managed,
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$LocaleHelper = '',
    [string]$RenderHelper = '',
    [string]$RenderHelperSha256 = '',
    [switch]$RuntimeOnly,
    [switch]$QaOrders,
    [switch]$PerfProbe
)
$ErrorActionPreference = 'Stop'
$inputRoot = [IO.Path]::GetFullPath((Join-Path $Managed '../..')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$outputPath = [IO.Path]::GetFullPath($Output).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if ($outputPath.StartsWith($inputRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be outside the original game directory' }
$sdk = (& dotnet --list-sdks | Select-Object -Last 1) -split ' ', 2
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate installed .NET SDK' }
$compiler = Join-Path ($sdk[1].Trim('[', ']')) ($sdk[0] + '/Roslyn/bincore/csc.dll')
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$runtime = Join-Path $Output 'ClosingShift.Android.dll'
$references = @('mscorlib.dll', 'System.Core.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.InputLegacyModule.dll', 'UnityEngine.AudioModule.dll', 'UnityEngine.VideoModule.dll', 'UnityEngine.PhysicsModule.dll', 'UnityEngine.UIModule.dll', 'UnityEngine.UI.dll', 'UnityEngine.TextRenderingModule.dll', 'UnityEngine.IMGUIModule.dll', 'UnityEngine.AndroidJNIModule.dll', 'Unity.Postprocessing.Runtime.dll', 'HBAO.Runtime.dll', 'Aura2_Core.dll', 'Unity.TextMeshPro.dll') | ForEach-Object { '/reference:' + (Join-Path $Managed $_) }
if (-not $LocaleHelper) { $LocaleHelper = Join-Path $Managed 'ClosingShift.RussianLocale.dll' }
$localeHelper = [IO.Path]::GetFullPath($LocaleHelper)
if (-not (Test-Path -LiteralPath $localeHelper)) { throw 'Build the Russian locale helper before the Android runtime shim' }
$references += '/reference:' + $localeHelper
if (-not $RenderHelper) {
    $RenderHelper = Join-Path $Managed 'ClosingShift.MobileRender.dll'
    $RenderHelperSha256 = 'fad0128ab73f426bc1a5c7ed0c81186ffc201640cd35ce830a60f4aae918b9cf'
}
$renderHelper = [IO.Path]::GetFullPath($RenderHelper)
if ($RenderHelperSha256 -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $renderHelper -Algorithm SHA256).Hash.ToLowerInvariant() -ne $RenderHelperSha256.ToLowerInvariant()) { throw 'Pinned mobile render helper changed or its explicit hash was omitted' }
$references += '/reference:' + $renderHelper
& 'C:\Program Files\Python313\python.exe' (Join-Path $PSScriptRoot 'generate_ui_routes.py')
if ($LASTEXITCODE -ne 0) { throw 'Verified UI route generation failed' }
& 'C:\Program Files\Python313\python.exe' -X utf8 -B (Join-Path $PSScriptRoot '../generate_room_occluders.py')
if ($LASTEXITCODE -ne 0) { throw 'Verified room occluder generation failed' }
$sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'Android*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
$defines = @()
if ($QaOrders) { $defines += '/define:CLOSING_SHIFT_QA' }
if ($PerfProbe) {
    $defines += '/define:CLOSING_SHIFT_PERF'
    $references += @('UnityEngine.ParticleSystemModule.dll', 'UnityEngine.AnimationModule.dll') | ForEach-Object { '/reference:' + (Join-Path $Managed $_) }
}
& dotnet $compiler /noconfig /nostdlib /target:library /langversion:7.3 /optimize+ /deterministic+ "/out:$runtime" $references $defines $sources
if ($LASTEXITCODE -ne 0) { throw 'Android runtime shim compilation failed' }
if ($RuntimeOnly) { Write-Output "ANDROID_RUNTIME_ONLY_PASS $runtime"; return }
& dotnet build (Join-Path $PSScriptRoot 'patch_managed.csproj') -c Release --configfile (Join-Path $PSScriptRoot 'NuGet.Config') --nologo
if ($LASTEXITCODE -ne 0) { throw 'dnlib patcher compilation failed' }
& dotnet (Join-Path $PSScriptRoot 'bin/Release/net10.0/patch_managed.dll') $Managed $Output $runtime
if ($LASTEXITCODE -ne 0) { throw 'Managed patch/self-check failed' }
