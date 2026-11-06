$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$vs = 'C:\Program Files\Microsoft Visual Studio\18\Community'
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$cmake = Join-Path $env:LOCALAPPDATA 'Android/Sdk/cmake/3.22.1/bin/cmake.exe'
$ninja = Join-Path $env:LOCALAPPDATA 'Android/Sdk/cmake/3.22.1/bin/ninja.exe'
$build = Join-Path $root 'work/compute_hlslcc_build'
& $cmake -S $PSScriptRoot -B $build -G Ninja "-DCMAKE_MAKE_PROGRAM=$ninja" '-DCMAKE_BUILD_TYPE=Release' "-DHLSLCC_SOURCE=$(Join-Path $root 'work/tools/HLSLcc')" "-DHLSLCC_LIBRARY=$(Join-Path $root 'work/hlslcc_build/hlslcc/hlslcc.lib')"
if ($LASTEXITCODE -ne 0) { throw 'Compute HLSLcc configure failed' }
& $cmake --build $build --parallel 2
if ($LASTEXITCODE -ne 0) { throw 'Compute HLSLcc build failed' }
