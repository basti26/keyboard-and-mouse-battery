$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach ($test in @('BatteryReaderTests', 'LogTests', 'MouseProtocolTests')) {
    $target = Join-Path $output "$test.exe"
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll "/main:$test" "/out:$target" "$PSScriptRoot\BatteryTray.cs" "$PSScriptRoot\$test.cs"
    if ($LASTEXITCODE -ne 0) { throw "测试编译失败：$test" }
    & $target
    if ($LASTEXITCODE -ne 0) { throw "测试失败：$test" }
}
