param()
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build-launcher.ps1')
$taskExecutable = Join-Path $PSScriptRoot 'bin\PebrelLauncher.exe'
$taskShell = New-Object -ComObject WScript.Shell
$taskLocations = @([Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('Programs'))
foreach ($taskLocation in $taskLocations) {
    if (-not $taskLocation) { throw 'Windows did not provide a shortcut directory.' }
    $taskShortcutPath = Join-Path $taskLocation 'Pebrel Terminal Hub.lnk'
    if (Test-Path -LiteralPath $taskShortcutPath) {
        $taskExisting = $taskShell.CreateShortcut($taskShortcutPath)
        if ($taskExisting.TargetPath -and ![string]::Equals($taskExisting.TargetPath, $taskExecutable, [StringComparison]::OrdinalIgnoreCase)) {
            throw ('An unrelated shortcut already exists: ' + $taskShortcutPath)
        }
    }
    $taskShortcut = $taskShell.CreateShortcut($taskShortcutPath)
    $taskShortcut.TargetPath = $taskExecutable
    $taskShortcut.WorkingDirectory = $PSScriptRoot
    $taskShortcut.IconLocation = $taskExecutable + ',0'
    $taskShortcut.Description = 'Pebrel UTF-8 terminal and COM11 / COM12 launcher'
    $taskShortcut.Save()
    Write-Host ('Shortcut: ' + $taskShortcutPath)
}
