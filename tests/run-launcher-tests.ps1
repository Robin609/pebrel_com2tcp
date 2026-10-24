param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskCandidate = Join-Path $PSScriptRoot 'bin\PebrelLauncher.exe'
& (Join-Path $taskRoot 'build-launcher.ps1') -OutputPath $taskCandidate
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$taskOutput = Join-Path $PSScriptRoot 'bin\PebrelLauncher.Tests.exe'
$taskIcon = Join-Path $taskRoot 'launcher\PebrelLauncher.ico'
$taskArguments = @('/nologo', '/target:exe', '/main:LauncherTests', '/platform:anycpu', '/optimize+', '/warn:4', '/codepage:65001', '/utf8output',
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll',
    ('/resource:' + $taskIcon + ',PebrelLauncher.ico'), ('/out:' + $taskOutput))
$taskArguments += '/resource:' + (Join-Path $taskRoot 'launcher\PebrelLauncher.png') + ',PebrelLauncher.png'
$taskArguments += @('LauncherProgram.cs', 'LauncherCore.cs', 'LauncherForm.cs') | ForEach-Object { Join-Path $taskRoot ('launcher\' + $_) }
$taskArguments += Join-Path $taskRoot 'ui\PebrelTheme.cs'
$taskArguments += Join-Path $PSScriptRoot 'LauncherTests.cs'
& $taskCompiler @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'Launcher tests compilation failed.' }
$taskStart = New-Object System.Diagnostics.ProcessStartInfo
$taskStart.FileName = $taskOutput
$taskStart.Arguments = '"' + $taskRoot + '"'
$taskStart.UseShellExecute = $false
$taskStart.CreateNoWindow = $true
$taskStart.RedirectStandardOutput = $true
$taskStart.RedirectStandardError = $true
$taskStart.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
$taskStart.StandardErrorEncoding = New-Object Text.UTF8Encoding($false)
$taskProcess = [System.Diagnostics.Process]::Start($taskStart)
$taskStdout = $taskProcess.StandardOutput.ReadToEndAsync()
$taskStderr = $taskProcess.StandardError.ReadToEndAsync()
$taskProcess.WaitForExit()
$taskLog = $taskStdout.Result + $taskStderr.Result
$taskLog | Tee-Object -FilePath (Join-Path $PSScriptRoot 'results\launcher-latest.txt')
$taskExitCode = $taskProcess.ExitCode
$taskProcess.Dispose()
if ($taskExitCode -ne 0) { throw 'Launcher tests failed. See tests\results\launcher-latest.txt.' }
