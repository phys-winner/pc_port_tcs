param([string]$Managed = 'external/game/TheClosingShift_Data\Managed')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $root 'work/android_compute/managed'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$sdk = (& dotnet --list-sdks | Select-Object -Last 1) -split ' ', 2
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate installed .NET SDK' }
$compiler = Join-Path ($sdk[1].Trim('[', ']')) ($sdk[0] + '/Roslyn/bincore/csc.dll')
$helper = Join-Path $output 'ClosingShift.AuraGles.dll'
$references = @('mscorlib.dll', 'UnityEngine.CoreModule.dll') | ForEach-Object { '/reference:' + (Join-Path $Managed $_) }
& dotnet $compiler /noconfig /nostdlib /target:library /langversion:7.3 /optimize+ /deterministic+ "/out:$helper" $references (Join-Path $PSScriptRoot 'AuraGlesFormats.cs')
if ($LASTEXITCODE -ne 0) { throw 'Aura format helper compilation failed' }
& dotnet build (Join-Path $PSScriptRoot 'patch_aura_formats.csproj') -c Release --configfile (Join-Path $PSScriptRoot 'NuGet.Config') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Aura dnlib patcher compilation failed' }
& dotnet (Join-Path $PSScriptRoot 'bin/Release/net10.0/patch_aura_formats.dll') (Join-Path $Managed 'Aura2_Core.dll') $helper (Join-Path $Managed 'UnityEngine.CoreModule.dll') (Join-Path $output 'Aura2_Core.dll')
if ($LASTEXITCODE -ne 0) { throw 'Aura producer format patch/self-check failed' }
