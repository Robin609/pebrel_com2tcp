param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskPebrel = Join-Path $taskRoot 'clients\pebrel-v2.2.0\pebrel.exe'
$taskRunner = Join-Path $PSScriptRoot 'bin\PebrelLauncher.Tests.exe'
$env:PEBREL_CONFIG_DIR = Join-Path $taskRoot 'clients\pebrel-data'

function Invoke-Pebrel([string[]]$Arguments) {
    $taskText = & $taskPebrel @Arguments 2>&1 | Out-String
    $taskReply = $taskText | ConvertFrom-Json
    if (-not $taskReply.ok) { throw ('Pebrel API failed: ' + $taskText) }
    return $taskReply.result
}
function Get-Panes {
    $taskSnapshot = Invoke-Pebrel @('ctl', 'snapshot', '--pretty')
    foreach ($taskWindow in $taskSnapshot.windows) {
        foreach ($taskTab in $taskWindow.tabs) {
            foreach ($taskPane in $taskTab.panes) {
                [pscustomobject]@{ Window = $taskWindow.id; Id = $taskPane.id; State = $taskPane.task_state }
            }
        }
    }
}

$taskBaseline = @(Get-Panes)
$taskOldIds = @($taskBaseline | ForEach-Object { $_.Id })
$taskListeners = @()
$taskClients = @()
$taskTestPanes = @()
$taskCases = @()
try {
    # Binding is exclusive: never stop or substitute a real service on these ports.
    foreach ($taskPort in @(2011, 2012)) {
        $taskListener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $taskPort)
        $taskListener.Start()
        $taskListeners += $taskListener
    }
    foreach ($taskIndex in @(0, 1)) {
        $taskMode = if ($taskIndex -eq 0) { 'Com11' } else { 'Com12' }
        $taskMarker = if ($taskIndex -eq 0) { 'PEBREL_LAUNCHER_COM11_OK' } else { 'PEBREL_LAUNCHER_COM12_OK' }
        $taskPending = $taskListeners[$taskIndex].BeginAcceptTcpClient($null, $null)
        & $taskRunner $taskRoot --launch $taskMode
        if ($LASTEXITCODE -ne 0) { throw ('Production launch failed: ' + $taskMode) }
        if (-not $taskPending.AsyncWaitHandle.WaitOne(15000)) { throw ('No real TCP connection for ' + $taskMode) }
        $taskClient = $taskListeners[$taskIndex].EndAcceptTcpClient($taskPending)
        $taskClients += $taskClient
        $taskPending.AsyncWaitHandle.Close()
        $taskUtf8Text = 'UTF8: ' + [char]0x4e2d + [char]0x6587 + ' ' + [char]0x25cf
        $taskBytes = [Text.Encoding]::UTF8.GetBytes("`r`n" + [char]27 + '[32m' + $taskMarker + [char]27 + '[0m ' + $taskUtf8Text + "`r`n")
        $taskClient.GetStream().Write($taskBytes, 0, $taskBytes.Length)
        $taskNewPane = $null
        $taskTimer = [Diagnostics.Stopwatch]::StartNew()
        while (-not $taskNewPane -and $taskTimer.ElapsedMilliseconds -lt 10000) {
            $taskNewPane = @(Get-Panes | Where-Object { $_.Id -notin $taskOldIds }) | Select-Object -First 1
            if (-not $taskNewPane) { Start-Sleep -Milliseconds 100 }
        }
        if (-not $taskNewPane) { throw ('No new Pebrel pane for ' + $taskMode) }
        $taskOldIds += $taskNewPane.Id
        $taskTestPanes += $taskNewPane
        $taskRead = $null
        $taskTimer.Restart()
        do {
            $taskRead = Invoke-Pebrel @('ctl', 'read', '--window', [string]$taskNewPane.Window, '--pane', [string]$taskNewPane.Id, '--lines', '60', '--pretty')
            if ($taskRead.text.Contains($taskMarker) -and $taskRead.text.Contains($taskUtf8Text)) { break }
            Start-Sleep -Milliseconds 100
        } while ($taskTimer.ElapsedMilliseconds -lt 6000)
        if (-not $taskRead.text.Contains($taskMarker) -or -not $taskRead.text.Contains($taskUtf8Text)) {
            throw ('TCP UTF-8 marker did not reach the real terminal: ' + $taskMode)
        }
        $taskProcesses = Invoke-Pebrel @('ctl', 'procs', '--window', [string]$taskNewPane.Window, '--pane', [string]$taskNewPane.Id, '--pretty')
        if (-not @($taskProcesses.processes | Where-Object { $_.executable -match '^ncat(\.exe)?$' }).Count) {
            throw ('Real Ncat process missing: ' + $taskMode)
        }
        $taskCases += [pscustomobject]@{ Mode = $taskMode; Port = (2011 + $taskIndex); TcpAccepted = $true; Utf8AndAnsiRendered = $true; Pane = $taskNewPane.Id; Processes = @($taskProcesses.processes | ForEach-Object { $_.executable }); Evidence = $taskMarker }
        Write-Host ('PASS real ' + $taskMode + ': profile -> PowerShell UTF-8 -> NcConsole -> Ncat -> TCP -> terminal grid')
    }
} finally {
    foreach ($taskClient in $taskClients) { $taskClient.Close() }
    foreach ($taskListener in $taskListeners) { $taskListener.Stop() }
    foreach ($taskPane in $taskTestPanes) {
        $taskTimer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $taskCurrent = Get-Panes | Where-Object { $_.Id -eq $taskPane.Id } | Select-Object -First 1
            if (-not $taskCurrent) { break }
            if ($taskCurrent.State -eq 'idle') {
                $null = Invoke-Pebrel @('ctl', 'close-pane', '--window', [string]$taskPane.Window, '--pane', [string]$taskPane.Id, '--pretty')
                break
            }
            Start-Sleep -Milliseconds 200
        } while ($taskTimer.ElapsedMilliseconds -lt 8000)
        if ($taskCurrent -and $taskCurrent.State -ne 'idle') { Write-Warning ('Retained test pane ' + $taskPane.Id + ': not idle; no forced close.') }
    }
}
[pscustomobject]@{ Date = (Get-Date -Format 's'); Scope = 'Real Pebrel and Ncat with temporary loopback servers; no serial device commands'; Cases = $taskCases } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'results\launcher-runtime.json') -Encoding UTF8
