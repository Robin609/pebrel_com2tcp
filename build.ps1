param([switch]$Run, [string]$OutputPath)

$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskSources = @('Program.cs', 'BridgeService.cs', 'MainForm.cs', 'ChannelPanel.cs', 'ui\PebrelTheme.cs') | ForEach-Object {
    Join-Path $taskRoot $_
}
$taskOutputDir = Join-Path $taskRoot 'bin'
$taskOutput = Join-Path $taskOutputDir 'Com2Tcp.exe'
if ($OutputPath) { $taskOutput = [IO.Path]::GetFullPath($OutputPath) }
$taskSourcesToCheck = $taskSources + (Join-Path $taskRoot 'launcher\PebrelLauncher.ico')
$taskCompilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)

try {
    $taskCompiler = $taskCompilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $taskCompiler) {
        throw 'Windows .NET Framework C# compiler was not found. .NET Framework 4.8 is required.'
    }
    $taskNeedsBuild = -not (Test-Path -LiteralPath $taskOutput)
    if (-not $taskNeedsBuild) {
        $taskOutputTime = (Get-Item -LiteralPath $taskOutput).LastWriteTimeUtc
        $taskNeedsBuild = @($taskSourcesToCheck | Where-Object {
            (Get-Item -LiteralPath $_).LastWriteTimeUtc -gt $taskOutputTime
        }).Count -gt 0
    }
    if ($taskNeedsBuild) {
        New-Item -ItemType Directory -Path $taskOutputDir -Force | Out-Null
        $taskCompilerArgs = @(
            '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/warn:4',
            '/codepage:65001', '/utf8output',
            '/reference:System.dll', '/reference:System.Core.dll',
            '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
            '/reference:System.Xml.dll', ('/out:' + $taskOutput),
            ('/win32icon:' + (Join-Path $taskRoot 'launcher\PebrelLauncher.ico'))
        ) + $taskSources
        & $taskCompiler @taskCompilerArgs
        if ($LASTEXITCODE -ne 0) {
            throw ('Compilation failed with exit code ' + $LASTEXITCODE + '.')
        }
        Write-Host ('Built: ' + $taskOutput)
    }
    $taskNcSource = Join-Path $taskRoot 'NcConsole.cs'
    $taskNcOutput = Join-Path $taskOutputDir 'NcConsole.exe'
    if (-not (Test-Path -LiteralPath $taskNcOutput) -or
        (Get-Item -LiteralPath $taskNcSource).LastWriteTimeUtc -gt (Get-Item -LiteralPath $taskNcOutput).LastWriteTimeUtc) {
        & $taskCompiler /nologo /target:exe /platform:anycpu /optimize+ /warn:4 /codepage:65001 /utf8output /reference:System.dll /reference:System.Core.dll ('/out:' + $taskNcOutput) $taskNcSource
        if ($LASTEXITCODE -ne 0) {
            throw ('nc launcher compilation failed with exit code ' + $LASTEXITCODE + '.')
        }
        Write-Host ('Built: ' + $taskNcOutput)
    }
    if ($Run) {
        Start-Process -FilePath $taskOutput -WorkingDirectory $taskRoot
    } else {
        Write-Host ('Executable: ' + $taskOutput)
    }
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
