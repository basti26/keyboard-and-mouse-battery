$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:"$PSScriptRoot\无线键鼠电量.exe" "$PSScriptRoot\BatteryTray.cs"
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
