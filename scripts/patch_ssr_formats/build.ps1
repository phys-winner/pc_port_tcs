param(
    [string]$Managed = '',
    [string]$Output = ''
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$Managed) { $Managed = 'external/game/TheClosingShift_Data/Managed' }
if (!$Output) { $Output = Join-Path $root 'work/android_ssr_inline/managed' }
New-Item -ItemType Directory -Force -Path $Output | Out-Null
& dotnet build (Join-Path $PSScriptRoot 'patch_ssr_formats.csproj') -c Release --configfile (Join-Path $PSScriptRoot 'NuGet.Config') --nologo
if ($LASTEXITCODE -ne 0) { throw 'SSR patcher build failed' }
& dotnet (Join-Path $PSScriptRoot 'bin/Release/net10.0/patch_ssr_formats.dll') (Join-Path $Managed 'Unity.Postprocessing.Runtime.dll') (Join-Path $Managed 'UnityEngine.CoreModule.dll') (Join-Path $Output 'Unity.Postprocessing.Runtime.dll')
if ($LASTEXITCODE -ne 0) { throw 'SSR patch/self-check failed' }
