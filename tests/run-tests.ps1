param([switch]$UiOnly)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) {
    $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$taskOutputDir = Join-Path $PSScriptRoot 'bin'
$taskResults = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Path $taskOutputDir, $taskResults -Force | Out-Null
$taskOutput = Join-Path $taskOutputDir 'Com2Tcp.Tests.exe'
$taskSources = @('Program.cs','BridgeService.cs','MainForm.cs','ChannelPanel.cs','ui\PebrelTheme.cs') | ForEach-Object { Join-Path $taskRoot $_ }
$taskSources += Join-Path $PSScriptRoot 'IntegrationTests.cs'
$taskArguments = @('/nologo','/target:exe','/main:Com2Tcp.Tests.TestRunner','/platform:anycpu','/optimize+','/warn:4','/codepage:65001','/utf8output',
    '/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Xml.dll','/reference:System.Xml.Linq.dll',('/out:' + $taskOutput)) + $taskSources
& $taskCompiler @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Integration test compilation failed.' }
$taskTestArguments = @()
if ($UiOnly) { $taskTestArguments += '--ui-only' }
$taskResultFile = if ($UiOnly) { 'pebrel-style-ui.txt' } else { 'latest.txt' }
& $taskOutput @taskTestArguments 2>&1 | Tee-Object -FilePath (Join-Path $taskResults $taskResultFile)
if ($LASTEXITCODE -ne 0) { throw ('Integration tests failed. See tests\results\' + $taskResultFile + '.') }
