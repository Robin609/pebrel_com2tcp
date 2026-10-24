param(
    [ValidateSet('Check','Build','Test','UiTest')][string]$Mode = 'Build',
    [string]$BuildRoot = 'D:\DevTools\Pebrel'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$toolchain = [IO.File]::ReadAllText((Join-Path $root 'rust-toolchain.toml'))
if ($toolchain -notmatch 'channel\s*=\s*"(\d+\.\d+\.\d+)"') { throw 'Expected a pinned Rust toolchain.' }
$version = $Matches[1]
$rustBin = Join-Path $BuildRoot ('rustup\toolchains\' + $version + '-x86_64-pc-windows-msvc\bin')
$cargo = Join-Path $rustBin 'cargo.exe'
$vcvars = Join-Path $BuildRoot 'MSVC\VC\Auxiliary\Build\vcvars64.bat'
foreach ($file in @($cargo, $vcvars)) { if (-not (Test-Path -LiteralPath $file)) { throw ('Missing build dependency: ' + $file) } }
$env:RUSTUP_HOME = Join-Path $BuildRoot 'rustup'
$env:CARGO_HOME = Join-Path $BuildRoot 'cargo'
$env:CARGO_TARGET_DIR = Join-Path $BuildRoot 'target'
$env:TEMP = Join-Path $BuildRoot 'tmp'
$env:TMP = $env:TEMP
$env:PATH = $rustBin + ';' + $env:PATH
$env:CARGO_PROFILE_DEV_DEBUG = '0'
$env:CARGO_PROFILE_TEST_DEBUG = '0'
New-Item -ItemType Directory -Path $env:TEMP -Force | Out-Null
if ($Mode -in @('Test', 'UiTest')) {
    $env:PEBREL_CONFIG_DIR = Join-Path $env:TEMP ('test-config-' + $PID)
    New-Item -ItemType Directory -Path $env:PEBREL_CONFIG_DIR -Force | Out-Null
}
$arguments = switch ($Mode) {
    'Check' { 'check -p nebula --bin pebrel --bin pebrel-serial --features serial-bridge' }
    'Build' { 'build -p nebula --bin pebrel --bin pebrel-serial -p nebula_hook --bin pebrel-hook --features nebula/serial-bridge --release --locked' }
    'Test' { 'test -p nebula --bin pebrel --features serial-bridge,gpui-test-support --locked -- serial_bridge:: gpui_shell::settings_pane::serial::tests:: settings_nav_ platform::distribution::tests:: --test-threads=1' }
    'UiTest' { 'test -p nebula --bin pebrel --features serial-bridge,gpui-test-support --locked gpui_shell::settings_pane::serial::tests:: -- --test-threads=1' }
}
Push-Location $root
try {
    # Keep developer-environment setup in its own process; never rewrite user PATH.
    $command = '""' + $vcvars + '" >nul && "' + $cargo + '" ' + $arguments + '"'
    & $env:ComSpec /d /s /c $command
    if ($LASTEXITCODE -ne 0) { throw ('Pebrel ' + $Mode + ' failed: ' + $LASTEXITCODE) }
} finally { Pop-Location }
