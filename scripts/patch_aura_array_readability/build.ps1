param([string]$Source = '', [string]$Output = '')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$Source) { $Source = Join-Path $root 'work/android_aura_shadow_memory/managed/Aura2_Core.dll' }
if (!$Output) { $Output = Join-Path $root 'work/android_aura_array_nocpu/managed/Aura2_Core.dll' }
& dotnet build (Join-Path $PSScriptRoot 'patch_aura_array_readability.csproj') -c Release --configfile (Join-Path $PSScriptRoot 'NuGet.Config') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Aura CPU backing patcher build failed' }
& dotnet (Join-Path $PSScriptRoot 'bin/Release/net10.0/patch_aura_array_readability.dll') $Source (Join-Path $root 'app/assets/bin/Data/Managed') $Output
if ($LASTEXITCODE -ne 0) { throw 'Aura CPU backing patch/self-check failed' }
