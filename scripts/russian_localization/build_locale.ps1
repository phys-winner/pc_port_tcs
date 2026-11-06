param([string]$Managed = 'external/game/TheClosingShift_Data\Managed',
      [string]$CanonicalManaged = '', [string]$Fonts = '',
      [string]$Output = '', [switch]$HelperOnly)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $CanonicalManaged) { $CanonicalManaged = Join-Path $root 'app/assets/bin/Data/Managed' }
if (-not $Fonts) { $Fonts = Join-Path $CanonicalManaged 'ClosingShift.RussianFonts.dll' }
if (-not $Output) { $Output = Join-Path $root 'work/russian_localization/managed' }
$output = [IO.Path]::GetFullPath($Output)
$workRoot = [IO.Path]::GetFullPath((Join-Path $root 'work')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not ($output.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar).StartsWith($workRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Locale build output must stay inside workspace work' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$fontHash = (Get-FileHash -LiteralPath $Fonts -Algorithm SHA256).Hash.ToLowerInvariant()
if ($fontHash -ne '3d31b76c285dc45fbfc26fd3f7ea8548633ba0f067ac4fe0c1af66970e9b5339') { throw 'Frozen Russian font helper changed' }
$sdk = (& dotnet --list-sdks | Select-Object -Last 1) -split ' ', 2
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate installed .NET SDK' }
$compiler = Join-Path ($sdk[1].Trim('[', ']')) ($sdk[0] + '/Roslyn/bincore/csc.dll')
& 'C:\Program Files\Python313\python.exe' (Join-Path $PSScriptRoot 'generate_ui_data.py')
if ($LASTEXITCODE -ne 0) { throw 'Russian UI data generation failed' }
$references = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.UIModule.dll', 'UnityEngine.TextRenderingModule.dll', 'UnityEngine.UI.dll', 'Unity.TextMeshPro.dll') | ForEach-Object { '/reference:' + (Join-Path $Managed $_) }
$sources = @('RussianAttribution.cs', 'RussianDefaultPolicy.cs', 'RussianLocaleDefaults.cs', 'RussianPresentationPolicy.cs', 'RussianUiData.cs', 'RussianUiPresentation.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& dotnet $compiler /noconfig /nostdlib /target:library /langversion:7.3 /optimize+ /deterministic+ "/out:$output/ClosingShift.RussianLocale.dll" $references "/reference:$Fonts" $sources
if ($LASTEXITCODE -ne 0) { throw 'Russian locale helper compilation failed' }
if ($HelperOnly) { Write-Output "RUSSIAN_LOCALE_HELPER_ONLY_PASS $output/ClosingShift.RussianLocale.dll"; return }
& dotnet build (Join-Path $PSScriptRoot 'patch_i2/patch_i2.csproj') -c Release --configfile (Join-Path $PSScriptRoot 'patch_i2/NuGet.Config') --nologo
if ($LASTEXITCODE -ne 0) { throw 'I2 patcher compilation failed' }
$source = Join-Path $CanonicalManaged 'Assembly-CSharp-firstpass.dll'
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
& dotnet (Join-Path $PSScriptRoot 'patch_i2/bin/Release/net10.0/patch_i2.dll') $source $sourceHash (Join-Path $output 'ClosingShift.RussianLocale.dll') (Join-Path $Managed 'UnityEngine.CoreModule.dll') (Join-Path $Managed 'UnityEngine.UI.dll') (Join-Path $output 'Assembly-CSharp-firstpass.dll') $root
if ($LASTEXITCODE -ne 0) { throw 'Guarded I2 patch failed' }
