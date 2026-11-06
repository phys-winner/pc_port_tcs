param(
    [string]$Game = '',
    [Parameter(Mandatory = $true)][string]$Runtime,
    [Parameter(Mandatory = $true)][string]$RenderHelper,
    [string]$Output = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $Game) {
    $Game = Join-Path $root 'work/a64-mobile-revision7/Managed/Assembly-CSharp.dll'
}
if (-not $Output) { $Output = Join-Path $root 'work/mobile_render8/managed' }
$project = Join-Path $root 'scripts/patch_managed/patch_managed.csproj'
& dotnet build $project -c Release --configfile (Join-Path $root 'scripts/patch_managed/NuGet.Config') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Managed render patcher compilation failed' }
& dotnet (Join-Path $root 'scripts/patch_managed/bin/Release/net10.0/patch_managed.dll') --render-scale8 $Game $Runtime $RenderHelper $Output
if ($LASTEXITCODE -ne 0) { throw 'Guarded render/fullscreen incremental patch failed' }
