param([string]$Managed = '', [string]$Output = '')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $Managed) { $Managed = Join-Path $root 'app/assets/bin/Data/Managed' }
$Managed = [IO.Path]::GetFullPath($Managed)
if (-not $Output) { $Output = Join-Path $root 'work/mobile_render' }
$output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$sdk = (& dotnet --list-sdks | Select-Object -Last 1) -split ' ', 2
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate installed .NET SDK' }
$compiler = Join-Path ($sdk[1].Trim('[', ']')) ($sdk[0] + '/Roslyn/bincore/csc.dll')
$mscorlib = Join-Path $Managed 'mscorlib.dll'
$core = Join-Path $Managed 'UnityEngine.CoreModule.dll'
foreach ($path in @($compiler, $mscorlib, $core)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required local compiler/reference missing: $path" }
}
$policy = Join-Path $PSScriptRoot 'PureScalePolicy.cs'
$library = Join-Path $output 'ClosingShift.MobileRender.dll'
& dotnet $compiler /noconfig /nostdlib /target:library /langversion:7.3 /optimize+ /deterministic+ "/out:$library" "/reference:$mscorlib" "/reference:$core" $policy (Join-Path $PSScriptRoot 'MobileRenderSettings.cs')
if ($LASTEXITCODE -ne 0) { throw 'Mobile render helper compilation failed' }
$selfcheck = Join-Path $output 'PolicySelfCheck.exe'
& dotnet $compiler /noconfig /nostdlib /target:exe /langversion:7.3 /optimize+ /deterministic+ "/out:$selfcheck" "/reference:$mscorlib" $policy (Join-Path $PSScriptRoot 'PolicySelfCheck.cs')
if ($LASTEXITCODE -ne 0) { throw 'Scale policy self-check compilation failed' }
$result = & $selfcheck
if ($LASTEXITCODE -ne 0) { throw 'Scale policy self-check failed' }
$result | Set-Content -LiteralPath (Join-Path $output 'policy-selfcheck.txt') -Encoding utf8
$runtimeCheck = Join-Path $output 'RuntimeSelfCheck.exe'
& dotnet $compiler /noconfig /nostdlib /target:exe /langversion:7.3 /optimize+ /deterministic+ "/out:$runtimeCheck" "/reference:$mscorlib" $policy (Join-Path $PSScriptRoot 'MobileRenderSettings.cs') (Join-Path $PSScriptRoot 'RuntimeSelfCheck.cs')
if ($LASTEXITCODE -ne 0) { throw 'Runtime fullscreen/render percentage self-check compilation failed' }
$runtimeResult = & $runtimeCheck
if ($LASTEXITCODE -ne 0) { throw 'Runtime fullscreen/render percentage self-check failed' }
$runtimeResult | Set-Content -LiteralPath (Join-Path $output 'runtime-selfcheck.txt') -Encoding utf8
$manifest = [ordered]@{
    dll = $library; sha256 = (Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash.ToLowerInvariant()
    references = @(@{ path = $mscorlib; sha256 = (Get-FileHash -LiteralPath $mscorlib -Algorithm SHA256).Hash.ToLowerInvariant() }, @{ path = $core; sha256 = (Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash.ToLowerInvariant() })
    selfcheck = [string]$result; runtimeSelfcheck = [string]$runtimeResult; canonicalWrites = $false; deviceWrites = $false
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8
$result
$runtimeResult
$manifest.sha256
