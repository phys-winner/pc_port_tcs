param([switch]$ToolsOnly)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$data = Join-Path $root 'work/tools/Unity2020/Editor/Data'
$output = Join-Path $root 'work/aot_compat'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$sdk = (& dotnet --list-sdks | Select-Object -Last 1) -split ' ', 2
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate local .NET compiler' }
$compiler = Join-Path ($sdk[1].Trim('[', ']')) ($sdk[0] + '/Roslyn/bincore/csc.dll')
$mscorlib = Join-Path $data 'MonoBleedingEdge/lib/mono/unityaot/mscorlib.dll'
$core = Join-Path $data 'PlaybackEngines/AndroidPlayer/Variations/il2cpp/Managed/UnityEngine.CoreModule.dll'
$helper = Join-Path $output 'ClosingShift.AotCompat.dll'
& dotnet $compiler /noconfig /nostdlib /target:library /langversion:7.3 /optimize+ /deterministic+ "/out:$helper" "/reference:$mscorlib" "/reference:$core" (Join-Path $PSScriptRoot 'AotReflectionFallbacks.cs')
if ($LASTEXITCODE -ne 0) { throw 'AOT reflection helper compile failed' }
& dotnet build (Join-Path $PSScriptRoot 'patch_aot/patch_aot.csproj') -c Release --configfile (Join-Path $PSScriptRoot 'patch_aot/NuGet.Config') --nologo
if ($LASTEXITCODE -ne 0) { throw 'AOT patcher host build failed' }
if ($ToolsOnly) { Write-Output "AOT_BUILD_TOOLS_PASS $helper"; return }
$original = 'external/game/TheClosingShift_Data/Managed'
$patches = Join-Path $output 'Managed'
& dotnet (Join-Path $PSScriptRoot 'patch_aot/bin/Release/net10.0/patch_aot.dll') $original $patches $helper
if ($LASTEXITCODE -ne 0) { throw 'Work-copy AOT compatibility patch failed' }
$test = Join-Path $output 'AotReflectionSelfCheck.exe'
& dotnet $compiler /noconfig /nostdlib /target:exe /langversion:7.3 /optimize+ /deterministic+ "/out:$test" "/reference:$mscorlib" "/reference:$helper" "/reference:$core" (Join-Path $PSScriptRoot 'AotReflectionSelfCheck.cs')
if ($LASTEXITCODE -ne 0) { throw 'AOT reflection self-check compile failed' }
$result = & $test $patches $original
if ($LASTEXITCODE -ne 0) { throw 'AOT reflection work-copy self-check failed' }
$result | Set-Content -LiteralPath (Join-Path $output 'selfcheck.txt') -Encoding utf8
$result
Get-FileHash -LiteralPath $helper -Algorithm SHA256 | Format-List Path,Hash
