param([switch]$Run, [string]$OutputPath)
$ErrorActionPreference = 'Stop'
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) {
    $taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw '.NET Framework 4.8 C# compiler is required.' }
$taskIcon = Join-Path $PSScriptRoot 'launcher\PebrelLauncher.ico'
$taskLogo = Join-Path $PSScriptRoot 'launcher\PebrelLauncher.png'
$taskIconSource = Join-Path $PSScriptRoot 'launcher\create-icon.ps1'
if (-not (Test-Path -LiteralPath $taskIcon) -or -not (Test-Path -LiteralPath $taskLogo) -or (Get-Item -LiteralPath $taskIconSource).LastWriteTimeUtc -gt (Get-Item -LiteralPath $taskIcon).LastWriteTimeUtc) {
    & $taskIconSource
}
$taskOutputDir = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $taskOutputDir -Force | Out-Null
$taskOutput = Join-Path $taskOutputDir 'PebrelLauncher.exe'
if ($OutputPath) { $taskOutput = [IO.Path]::GetFullPath($OutputPath) }
$taskSources = @('LauncherProgram.cs', 'LauncherCore.cs', 'LauncherForm.cs') | ForEach-Object { Join-Path $PSScriptRoot ('launcher\' + $_) }
$taskSources += Join-Path $PSScriptRoot 'ui\PebrelTheme.cs'
$taskDependencies = @($taskSources) + $taskIcon + $taskLogo + (Join-Path $PSScriptRoot 'launcher\launcher.manifest')
$taskNeedsBuild = -not (Test-Path -LiteralPath $taskOutput)
if (-not $taskNeedsBuild) {
    $taskOutputTime = (Get-Item -LiteralPath $taskOutput).LastWriteTimeUtc
    $taskNeedsBuild = @($taskDependencies | Where-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc -gt $taskOutputTime }).Count -gt 0
}
if ($taskNeedsBuild) {
    $taskArguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/warn:4', '/codepage:65001', '/utf8output',
        '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll',
        ('/win32icon:' + $taskIcon), ('/resource:' + $taskIcon + ',PebrelLauncher.ico'),
        ('/resource:' + $taskLogo + ',PebrelLauncher.png'),
        ('/win32manifest:' + (Join-Path $PSScriptRoot 'launcher\launcher.manifest')), ('/out:' + $taskOutput)) + $taskSources
    & $taskCompiler @taskArguments
    if ($LASTEXITCODE -ne 0) { throw 'Pebrel launcher compilation failed.' }
}
Write-Host ('Application: ' + $taskOutput)
if ($Run) { Start-Process -FilePath $taskOutput -WorkingDirectory $PSScriptRoot }
