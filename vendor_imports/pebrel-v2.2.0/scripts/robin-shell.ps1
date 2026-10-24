[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateRange(0, 65535)]
    [int]$TcpPort = 0,
    [string]$TcpAddress = '127.0.0.1',
    [switch]$NoExit,
    [string]$ExecutionPolicy,
    [string]$Command,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ShellIntegrationArguments = @()
)

# Pebrel appends its normal PowerShell startup switches after a custom -File.
# Accept that exact dot-source operation without treating its path as TcpPort.
if ($Command -or $ShellIntegrationArguments.Count -gt 0) {
    if ($Command -ne '.' -or $ShellIntegrationArguments.Count -ne 1) {
        throw 'Unsupported Pebrel shell-integration arguments.'
    }
    $taskPromptPath = [IO.Path]::GetFullPath($ShellIntegrationArguments[0].Trim([char]39))
    $taskIntegrationRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'shell-integration')) + '\'
    if (!$taskPromptPath.StartsWith($taskIntegrationRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($taskPromptPath) -ne 'pebrel_prompt.ps1' -or
        !(Test-Path -LiteralPath $taskPromptPath -PathType Leaf)) {
        throw 'Unexpected Pebrel shell-integration script path.'
    }
    . $taskPromptPath
}

# Set both PowerShell's cached encodings and the native console code pages.
# Calling chcp alone can leave Windows PowerShell's cached encoding unchanged.
$taskUtf8Encoding = New-Object System.Text.UTF8Encoding($false)
[Console]::InputEncoding = $taskUtf8Encoding
[Console]::OutputEncoding = $taskUtf8Encoding
$global:OutputEncoding = $taskUtf8Encoding

if ($TcpPort -gt 0) {
    # The console entry point attaches to ConPTY and PowerShell waits for it.
    & (Join-Path $PSScriptRoot '..\runtime\pebrel-serial.exe') --host $TcpAddress --port ([string]$TcpPort)
}
