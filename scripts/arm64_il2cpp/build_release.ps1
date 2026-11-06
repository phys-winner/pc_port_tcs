param(
    [Parameter(Mandatory = $true)][string]$BuildDirectory,
    [string]$AndroidJar = (Join-Path $env:LOCALAPPDATA 'Android/Sdk/platforms/android-37.0/android.jar'),
    [switch]$QaOrders,
    # Diagnostic-only remote performance probe (AndroidPerfProbe); never use for a published build.
    [switch]$PerfProbe
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$build = [IO.Path]::GetFullPath((Join-Path $root $BuildDirectory))
$work = [IO.Path]::GetFullPath((Join-Path $root 'work')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $build.StartsWith($work, [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $build)) { throw 'Choose a new build directory inside this project work' }
$python = 'C:/Program Files/Python313/python.exe'
& dotnet build (Join-Path $PSScriptRoot 'inspect_managed/inspect_managed.csproj') -c Release --configfile (Join-Path $PSScriptRoot 'inspect_managed/NuGet.Config') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Managed inspector build failed' }
& (Join-Path $PSScriptRoot 'build_aot_compat.ps1') -ToolsOnly
if ($LASTEXITCODE -ne 0) { throw 'AOT build tools failed' }
$pipeline = Join-Path $PSScriptRoot 'actual_build.py'
$runtimeArgs = @()
if ($PerfProbe -and -not $QaOrders) { throw 'The diagnostic performance probe is built only into a QA runtime' }
if ($QaOrders) {
    $qaRuntime = $build + '-qa-runtime'
    if (Test-Path -LiteralPath $qaRuntime) { throw 'Choose a new QA runtime directory' }
    & (Join-Path $root 'scripts/patch_managed/build.ps1') -Managed (Join-Path $root 'app/assets/bin/Data/Managed') -Output $qaRuntime -RuntimeOnly -QaOrders -PerfProbe:$PerfProbe
    if ($LASTEXITCODE -ne 0) { throw 'QA runtime compilation failed' }
    $runtimeArgs = @('--android-runtime', (Join-Path $qaRuntime 'ClosingShift.Android.dll'))
}
& $python -X utf8 -B $pipeline --prepare --controls-frozen --build-dir $build $runtimeArgs
if ($LASTEXITCODE -ne 0) { throw 'Frozen ARM64 preparation failed' }
foreach ($stage in @('--convert', '--compile', '--stage-app')) {
    & $python -X utf8 -B $pipeline $stage --build-dir $build
    if ($LASTEXITCODE -ne 0) { throw "ARM64 stage failed: $stage" }
}
$unityJar = Join-Path $root 'work/tools/Unity2020/Editor/Data/PlaybackEngines/AndroidPlayer/Variations/il2cpp/Release/Classes/classes.jar'
& $python -X utf8 -B (Join-Path $root 'scripts/build_apk.py') --app (Join-Path $build 'app') --unity-jar $unityJar --android-jar $AndroidJar --output (Join-Path $build 'package/base.apk') --compress
if ($LASTEXITCODE -ne 0) { throw 'Signed APK packaging failed' }
