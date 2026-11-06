param([string]$Managed = 'external/game/TheClosingShift_Data\Managed')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = Join-Path $root 'work/font_candidate/managed'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$sdk = (& dotnet --list-sdks | Select-Object -Last 1) -split ' ', 2
if ($LASTEXITCODE -ne 0) { throw 'Cannot locate installed .NET SDK' }
$compiler = Join-Path ($sdk[1].Trim('[', ']')) ($sdk[0] + '/Roslyn/bincore/csc.dll')
$references = @('mscorlib.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.UIModule.dll', 'UnityEngine.UI.dll', 'UnityEngine.TextRenderingModule.dll', 'UnityEngine.TextCoreModule.dll', 'Unity.TextMeshPro.dll') | ForEach-Object { '/reference:' + (Join-Path $Managed $_) }
& dotnet $compiler /noconfig /nostdlib /target:library /langversion:7.3 /optimize+ /deterministic+ "/out:$output/ClosingShift.RussianFonts.dll" $references (Join-Path $PSScriptRoot 'RussianFontCompat.cs') (Join-Path $PSScriptRoot 'RussianGlyphProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Russian font helper compilation failed' }
