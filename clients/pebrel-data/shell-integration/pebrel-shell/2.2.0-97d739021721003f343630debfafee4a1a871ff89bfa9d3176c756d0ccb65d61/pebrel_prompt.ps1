
$global:NebE = [char]27
$global:PSDefaultParameterValues['Get-Content:Encoding'] = 'utf8'
$global:NebArrow = [char]0xE0B0
$global:NebLeftRound = [char]0xE0B6
$global:NebRightRound = [char]0xE0B4
$global:NebPromptArrow = [char]0x276F
$global:NebFolderIcon = [char]0xE70F
$global:NebGitBranchIcon = [char]0xF418
$global:NebClockIcon = [char]0xF017
$global:NebulaPromptCount = 0
$global:PebrelSettingsFile = if ($env:PEBREL_CONFIG_DIR) {
    Join-Path $env:PEBREL_CONFIG_DIR 'pebrel_settings.txt'
} elseif ($env:NEBULA_CONFIG_DIR) {
    Join-Path $env:NEBULA_CONFIG_DIR 'pebrel_settings.txt'
} elseif ($env:APPDATA) {
    Join-Path $env:APPDATA 'Pebrel\pebrel_settings.txt'
} elseif ($env:USERPROFILE) {
    Join-Path $env:USERPROFILE 'Pebrel\pebrel_settings.txt'
} else {
    Join-Path ([System.IO.Path]::GetTempPath()) 'Pebrel\pebrel_settings.txt'
}
if (-not (Test-Path -LiteralPath $global:PebrelSettingsFile)) {
    $legacy = Join-Path (Split-Path -Parent $global:PebrelSettingsFile) 'nebula_settings.txt'
    if (Test-Path -LiteralPath $legacy) { $global:PebrelSettingsFile = $legacy }
}
$global:NebulaSettingsFile = $global:PebrelSettingsFile

function global:Get-NebulaSetting {
    param([string]$Key, [string]$Default)

    try {
        if (Test-Path -LiteralPath $NebulaSettingsFile) {
            foreach ($line in Get-Content -LiteralPath $NebulaSettingsFile -ErrorAction SilentlyContinue) {
                $pair = $line -split '=', 2
                if ($pair.Count -eq 2 -and $pair[0].Trim() -eq $Key) {
                    return $pair[1].Trim()
                }
            }
        }
    } catch {}

    $Default
}

function global:Get-NebulaBoolSetting {
    param([string]$Key, [bool]$Default)

    $fallback = if ($Default) { '1' } else { '0' }
    $value = (Get-NebulaSetting $Key $fallback).ToLowerInvariant()
    switch ($value) {
        '1'     { return $true }
        'true'  { return $true }
        'yes'   { return $true }
        'on'    { return $true }
        '0'     { return $false }
        'false' { return $false }
        'no'    { return $false }
        'off'   { return $false }
        default { return $Default }
    }
}

# Rust selects and validates the proxy. SOCKS URLs stay in the child environment;
# only the explicit HTTP marker configures PowerShell's HTTP request cmdlets.
& {
    if (-not $env:PEBREL_HTTP_PROXY) { return }
    try {
        $address = [Uri]$env:PEBREL_HTTP_PROXY
        if ($address.Scheme -ne 'http') { throw 'Expected an HTTP proxy' }
        $builder = [UriBuilder]$address
        $builder.UserName = ''
        $builder.Password = ''
        $proxyUrl = $builder.Uri.AbsoluteUri
        $credential = $null
        if ($address.UserInfo) {
            $parts = $address.UserInfo.Split([char[]]@(':'), 2)
            $user = [Uri]::UnescapeDataString($parts[0])
            $password = if ($parts.Length -gt 1) { [Uri]::UnescapeDataString($parts[1]) } else { '' }
            $secret = New-Object System.Security.SecureString
            foreach ($character in $password.ToCharArray()) { $secret.AppendChar($character) }
            $secret.MakeReadOnly()
            $credential = New-Object System.Management.Automation.PSCredential($user, $secret)
        }
        foreach ($command in @('Invoke-WebRequest', 'Invoke-RestMethod')) {
            $global:PSDefaultParameterValues["${command}:Proxy"] = $proxyUrl
            $global:PSDefaultParameterValues.Remove("${command}:ProxyCredential")
            if ($credential) {
                $global:PSDefaultParameterValues["${command}:ProxyCredential"] = $credential
            }
        }
        $pebrelIsPwsh = ($PSVersionTable.PSEdition -eq 'Core') -or ($PSVersionTable.PSVersion.Major -ge 6)
        if (-not $pebrelIsPwsh) {
            $proxy = New-Object System.Net.WebProxy($proxyUrl, $true)
            if ($credential) { $proxy.Credentials = $credential.GetNetworkCredential() }
            [System.Net.WebRequest]::DefaultWebProxy = $proxy
        }
    } catch {
        # Do not print a URL or exception that could contain proxy credentials.
        Write-Warning 'Could not configure the terminal HTTP proxy.'
    }
}

# 用户自己的提示符（$PROFILE 里的 oh-my-posh / starship / 手写 prompt）是不是
# 已经就位。判据只能看函数体：PowerShell 内置 prompt 固定引用
# $executionContext.SessionState.Path.CurrentLocation；而 oh-my-posh 那一类是经
# Invoke-Expression 装进来的，ScriptBlock.File 为空，靠 File 判断会把它们全部
# 误判成内置提示符。
function global:Test-NebulaUserPrompt {
    param($ScriptBlock)

    if (-not $ScriptBlock) { return $false }
    $body = $ScriptBlock.ToString()
    # 重复 source 本脚本时看到的是 Nebula 自己的 wrapper，它不算用户提示符。
    if ($body.Contains('NebulaPreviousPrompt')) { return $false }
    return -not $body.Contains('$executionContext.SessionState.Path.CurrentLocation')
}

# 脚本可能在已有集成之后被 source。只在第一次安装时保存原 prompt，
# 之后重载 Nebula 脚本也不能把自己的 wrapper 当成“用户 prompt”递归调用。
$existingPrompt = Get-Command prompt -CommandType Function -ErrorAction SilentlyContinue
if (-not $global:NebulaPromptInstalled) {
    $global:NebulaPreviousPrompt = if ($existingPrompt) { $existingPrompt.ScriptBlock } else { $null }
    # 视觉归属。用户已经有提示符时，Nebula 只补协议标记，不画自己的 powerline：
    # 终端只负责补充协议标记，不替 shell 决定提示符外观。
    $global:NebulaUserOwnsPrompt = Test-NebulaUserPrompt $global:NebulaPreviousPrompt
    $global:NebulaPromptInstalled = $true
} elseif ($existingPrompt -and $existingPrompt.ScriptBlock.Module -and
    $existingPrompt.ScriptBlock.ToString().Contains('NebulaPreviousPrompt')) {
    # Re-sourcing after venv deactivation must retain the restored closure's owner.
    $variables = $existingPrompt.ScriptBlock.Module.SessionState.PSVariable
    $global:NebulaPreviousPrompt = $variables.GetValue('NebulaPreviousPrompt')
    $global:NebulaUserOwnsPrompt = [bool]$variables.GetValue('NebulaUserOwnsPrompt')
}

# A shell instance owns its completion context; children must get a new token.
if (-not $global:PebrelShellToken) {
    $global:PebrelShellToken = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(
        "pwsh:$PID`:$([Guid]::NewGuid().ToString('N'))"))
}

$global:NebulaPromptTemplate = {
    # Same principle as Oh My Posh: prompt rendering may execute external
    # commands, so preserve the previous command status. Errors inside the
    # prompt must stay silent — but ONLY inside: assigning here is scoped to
    # this function. (A top-level assignment once silenced the whole session,
    # eating every user-facing error, e.g. a failed `cd`.)
    $originalDollarQuestion = $global:?
    $originalLastExitCode = $global:LASTEXITCODE
    $outermost = -not $global:NebulaPromptRenderDepth
    $global:NebulaPromptRenderDepth = [int]$global:NebulaPromptRenderDepth + 1
    try {
        $ErrorActionPreference = 'SilentlyContinue'
        if ($outermost) {
            [Console]::Write("$([char]27)]1337;SetUserVar=pebrel_shell=$global:PebrelShellToken$([char]7)")
            if ($global:PebrelCompletionInputReady) { [Console]::Write("$([char]27)]1337;SetUserVar=pebrel_editor_ready=$global:PebrelEditorReady$([char]7)") }
            $global:NebulaPromptCount = [int]$global:NebulaPromptCount + 1
            $global:NebulaLastCommandSucceeded = $originalDollarQuestion
            $global:NebulaLastCommandExitCode = $originalLastExitCode
            $global:NebulaLastCommandDurationMs = $null
            try {
                $lastHistory = Get-History -Count 1 -ErrorAction SilentlyContinue
                if ($lastHistory -and $lastHistory.EndExecutionTime -ge $lastHistory.StartExecutionTime) {
                    $global:NebulaLastCommandDurationMs = [math]::Max(
                        0,
                        [math]::Round(($lastHistory.EndExecutionTime - $lastHistory.StartExecutionTime).TotalMilliseconds)
                    )
                }
            } catch {}
        }

        # 旧 prompt 的环境管理器、历史或目录 hook 等副作用要保留。它的视觉输出则
        # 分两种：用户自己的提示符继续可见（Nebula 只在它前面补协议标记），内置的
        # 默认提示符才丢掉、由 Nebula 渲染——否则两个提示符会叠在一起。
        $userPrompt = ''
        if ($script:NebulaPreviousPrompt -and -not $script:NebulaPreviousPromptRunning) {
            $script:NebulaPreviousPromptRunning = $true
            try {
                $global:LASTEXITCODE = $originalLastExitCode
                if (-not $originalDollarQuestion) {
                    Write-Error '' -ErrorAction Ignore
                }
                $previousOutput = & $script:NebulaPreviousPrompt
                if ($script:NebulaUserOwnsPrompt) {
                    # 提示符函数可以返回多个对象，宿主是按顺序拼起来显示的。
                    $userPrompt = (@($previousOutput) | ForEach-Object { [string]$_ }) -join ''
                }
            } catch {
            } finally {
                $script:NebulaPreviousPromptRunning = $false
            }
        }

        $e = $NebE
        # OSC 133;D;<code> — the previous command just finished (this prompt proves
        # it), carrying its exit status for the assistant's error recovery. `$?` is
        # the arbiter (it is False for BOTH failed cmdlets and non-zero native
        # commands, and unlike $LASTEXITCODE it resets every command — a stale
        # non-zero $LASTEXITCODE after a successful cmdlet must not read as
        # failure). The code detail comes from $LASTEXITCODE when it agrees,
        # otherwise a plain 1. First prompt of a session: $? is True → 0.
        $nebExit = 0
        if (-not $originalDollarQuestion) {
            $nebExit = if ($null -ne $originalLastExitCode -and $originalLastExitCode -ne 0) { $originalLastExitCode } else { 1 }
        }
        if ($outermost) { [Console]::Write("$e]133;D;$nebExit$([char]7)") }
        $reset = "$e[0m"
        $cwd = (Get-Location).Path
        $loc = $cwd
        $hp = $env:USERPROFILE
        # `loc` 只负责提示符的紧凑显示；功能协议必须发送绝对 cwd。否则 home
        # 下的 `~\repo` 会被宿主文件树与 Git 当成无法解析的相对路径。
        if ($hp -and $loc.StartsWith($hp)) { $loc = '~' + $loc.Substring($hp.Length) }
        $branch = ''
        $b = git rev-parse --abbrev-ref HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $b) { $branch = $b }
        $time = Get-Date -Format 'HH:mm:ss'

        $boundary = if ($outermost) { "$e]133;A$([char]7)$e]2;NEBULA|$cwd|$branch$([char]7)" } else { '' }
        $leadingNewline = ''
        try {
            # PowerShell cursor Y is zero-based. Like Oh My Posh's cancelNewline,
            # do not add a leading spacer for the first prompt or when at top.
            if ($global:NebulaPromptCount -gt 1 -and $Host.UI.RawUI.CursorPosition.Y -gt 0) {
                $leadingNewline = "`n"
            }
        } catch {
            if ($global:NebulaPromptCount -gt 1) { $leadingNewline = "`n" }
        }

        # 使用主题的 ANSI-16 索引，历史提示符仍随主题换色；不占用应用所需的
        # xterm 扩展色槽。路径和时间用默认前景/背景，避免浅色主题的 ANSI 灰阶低对比。

        if ($userPrompt) {
            # 视觉全部来自用户提示符；Nebula 只补协议：133;A 标出提示符起点，标题
            # 里带上宿主需要的绝对 cwd 与分支。换行留给用户提示符自己决定。
            $output = "$boundary$userPrompt"
        } elseif (-not (Get-NebulaBoolSetting 'powerline' $true)) {
            $branchText = if ($branch) { " ($branch)" } else { "" }
            $output = "$leadingNewline$boundary$e[38;5;6m$loc$branchText $e[35m$NebPromptArrow $reset"
        } else {
            $segs = New-Object System.Collections.ArrayList
            [void]$segs.Add(@{ bg=4; fg='38;5;0'; t=" $NebFolderIcon " })
            [void]$segs.Add(@{ bg=$null; fg='39'; t="  $loc  " })
            if ($branch) { [void]$segs.Add(@{ bg=$null; fg='38;5;6'; t=" $NebGitBranchIcon $branch  " }) }
            [void]$segs.Add(@{ bg=$null; fg='39'; t=" $NebClockIcon $time  " })

            # 49 = default background on both caps: the cap cell's square corners
            # always match the real terminal bg (any theme / wallpaper).
            $out = "$reset$e[38;5;$($segs[0].bg)m$e[49m$NebLeftRound$reset"
            for ($i = 0; $i -lt $segs.Count; $i++) {
                $s = $segs[$i]
                $bg = if ($null -eq $s.bg) { '49' } else { "48;5;$($s.bg)" }
                $out += "$e[${bg}m$e[$($s.fg)m$($s.t)"
                if ($null -ne $s.bg) {
                    $out += "$reset$e[38;5;$($s.bg)m$e[49m$NebArrow$reset"
                } else {
                    $out += $reset
                }
            }
            $output = "$leadingNewline$boundary$out`n`n$e[35m$NebPromptArrow $reset"
        }

        if ($outermost) {
            try { Set-PSReadLineOption -ExtraPromptLineCount (($output | Measure-Object -Line).Lines - 1) } catch {}
        }

        # prompt 返回值先输出，状态恢复必须放到整个函数的最后；否则任意一次
        # Measure-Object、git 或赋值都会让下一条命令看到错误的 $?。
        $output
    } finally { $global:NebulaPromptRenderDepth-- }
    $global:LASTEXITCODE = $originalLastExitCode
    if ($global:? -ne $originalDollarQuestion) {
        if ($originalDollarQuestion) {
            $null = 1
        } else {
            Write-Error '' -ErrorAction Ignore
        }
    }
}

# Each saved prompt retains its original owner when an environment restores it.
function global:Install-NebulaPrompt {
    param([scriptblock]$PreviousPrompt, [bool]$UserOwnsPrompt)

    $NebulaPreviousPrompt = $PreviousPrompt
    $NebulaUserOwnsPrompt = $UserOwnsPrompt
    $NebulaPreviousPromptRunning = $false
    $global:NebulaPreviousPrompt = $PreviousPrompt
    $global:NebulaUserOwnsPrompt = $UserOwnsPrompt
    $global:NebulaPromptScriptBlock = $global:NebulaPromptTemplate.GetNewClosure()
    Set-Item -Path function:global:prompt -Value $global:NebulaPromptScriptBlock
}
Install-NebulaPrompt $global:NebulaPreviousPrompt $global:NebulaUserOwnsPrompt

# Build a spec-correct file:// URI from a Windows path for OSC 8 hyperlinks.
# RFC 3986: escape every segment (UTF-8 + surrogate pairs via EscapeDataString),
# keep '/' as the separator and a leading "D:" drive as-is. UNC \\server\share
# becomes file://server/share/...; local paths become file:///D:/...
function global:ConvertTo-NebulaFileUri {
    param([string]$Path)

    # UNC (\\server\share\x): the first two segments are the authority; strip
    # the leading backslashes so empty split segments don't inflate the slashes.
    $isUnc = $Path.StartsWith('\\')
    $body = if ($isUnc) { $Path.Substring(2) } else { $Path }
    $escaped = (($body -replace '\\','/') -split '/' | ForEach-Object {
        if ($_ -match '^[A-Za-z]:$') { $_ } else { [System.Uri]::EscapeDataString($_) }
    }) -join '/'

    if ($isUnc) { 'file://' + $escaped } else { 'file:///' + $escaped }
}

# Unix-style colored directory listing, replacing PowerShell's default table.
function global:Nebula-List {
    $e = [char]27
    # 颜色统一走 ANSI-16 索引：终端主题表（Rust theme.rs → apply_term_colors）
    # 是唯一色源，浅/深主题切换时这里自动跟随，不再散落硬编码 RGB。
    # 37=元信息  90=次要(大小/日期)  34=目录  32=可执行  39=普通文件(默认前景)
    $meta = "$e[37m"
    $muted = "$e[90m"
    $argList = @($args | Where-Object { "$_" -notlike '-*' })
    $target = if ($argList.Count -gt 0) { $argList[0] } else { '.' }
    $items = Get-ChildItem -Force $target -ErrorAction SilentlyContinue |
        Sort-Object @{ Expression = { -not $_.PSIsContainer } }, Name
    foreach ($i in $items) {
        $isDir = $i.PSIsContainer
        if ($isDir) {
            $mode = 'drwxr-xr-x'
            $size = '     -'
            $col  = "$e[34m"
        } else {
            $mode = '-rw-r--r--'
            $len = $i.Length
            if ($len -ge 1048576) { $size = '{0,5:N1}M' -f ($len / 1048576) }
            elseif ($len -ge 1024) { $size = '{0,5:N1}K' -f ($len / 1024) }
            else { $size = '{0,6}' -f $len }
            # 设计稿：普通文件用默认前景（深灰近黑），可执行类才上绿色。
            $col = if ($i.Extension -match '^\.(exe|dll|bat|cmd|ps1|com|msi|sh)$') { "$e[32m" } else { "$e[39m" }
        }
        $date = '{0,12}' -f $i.LastWriteTime.ToString('MMM d HH:mm', [System.Globalization.CultureInfo]::InvariantCulture)
        # OSC 8 hyperlink around the name (nushell's osc8 behaviour): the
        # terminal turns it into a click target that opens the file/folder.
        # Full RFC 3986 encoding (UTF-8, CJK, spaces) via ConvertTo-NebulaFileUri.
        $uri = ConvertTo-NebulaFileUri $i.FullName
        $b = [char]7
        "$meta$mode$e[0m  $muted$size$e[0m  $muted$date$e[0m  $e]8;;$uri$b$col$($i.Name)$e[0m$e]8;;$b"
    }
}
Remove-Item Alias:ls  -Force -ErrorAction SilentlyContinue
Remove-Item Alias:dir -Force -ErrorAction SilentlyContinue
Remove-Item Alias:ll  -Force -ErrorAction SilentlyContinue
Set-Alias -Name ls  -Value Nebula-List -Scope Global -Option AllScope -Force
Set-Alias -Name ll  -Value Nebula-List -Scope Global -Option AllScope -Force
Set-Alias -Name dir -Value Nebula-List -Scope Global -Option AllScope -Force

function global:Convert-NebulaBareEnvAssignment {
    param([string]$Line)

    # PowerShell 的赋值右侧是表达式，$env:KEY=sk-ant-xxx 这类裸 token 会被当命令/表达式解析。
    # 这里仅兼容单行、纯字面量 token；复杂表达式仍交给 PowerShell 原生解析，避免误改用户命令。
    if ([string]::IsNullOrWhiteSpace($Line) -or $Line.Contains("`n") -or $Line.Contains("`r")) {
        return $null
    }

    $pattern = '^(?<indent>\s*)\$env:(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>[^''"`$@\(\[\{;|&<>#\s][^;|&<>`]*)\s*$'
    if ($Line -notmatch $pattern) {
        return $null
    }

    $value = $Matches['value'].Trim()
    if ([string]::IsNullOrEmpty($value)) {
        return $null
    }

    $escaped = $value.Replace("'", "''")
    return ($Matches['indent'] + '$env:' + $Matches['name'] + "='" + $escaped + "'")
}

function global:Convert-NebulaBareCd {
    param([string]$Line)

    # `cd D:/Program Files/` — an unquoted path with spaces splats into two
    # positional args and Set-Location errors out. People paste paths like
    # this constantly, so quote the whole remainder when it's a plain literal
    # (no quotes/variables/operators that PowerShell should parse itself).
    if ([string]::IsNullOrWhiteSpace($Line) -or $Line.Contains("`n") -or $Line.Contains("`r")) {
        return $null
    }

    $pattern = '^(?<indent>\s*)(?<cmd>cd|chdir|pushd|sl|Set-Location)\s+(?<path>[^''"`$;|&<>()\[\]{}-][^''"`$;|&<>]*\s[^''"`$;|&<>]*?)\s*$'
    if ($Line -notmatch $pattern) {
        return $null
    }

    $path = $Matches['path'].Trim()
    if ([string]::IsNullOrEmpty($path)) {
        return $null
    }

    $escaped = $path.Replace("'", "''")
    return ($Matches['indent'] + $Matches['cmd'] + " '" + $escaped + "'")
}

# Keep the user's PSReadLine prediction configuration. The completion adapter
# yields when the shell has text after the cursor, including native inline
# predictions. Disabling Pebrel completion must not disable the shell's editor.
if (Get-Command Set-PSReadLineOption -ErrorAction SilentlyContinue) {
    try {
        # 不让 PowerShell 的 continuation prompt 回退成突兀的 `>>`，视觉上保持 Nebula 的单箭头。
        # 35=Magenta：主题表里的提示符色（浅色=优雅紫 #8250df），与主提示符一致。
        Set-PSReadLineOption -ContinuationPrompt "$([char]27)[35m$NebPromptArrow $([char]27)[0m" -ErrorAction SilentlyContinue
    } catch {}
    try {
        # 语法高亮同样只用 ANSI-16 索引——色值由终端主题表决定，浅/深自动适配。
        Set-PSReadLineOption -Colors @{
            Command          = "$([char]27)[96m"
            Parameter        = "$([char]27)[95m"
            String           = "$([char]27)[32m"
            Number           = "$([char]27)[33m"
            Operator         = "$([char]27)[37m"
            Variable         = "$([char]27)[94m"
            Comment          = "$([char]27)[90m"
        } -ErrorAction SilentlyContinue
    } catch {}
    try {
        Set-PSReadLineKeyHandler -Key Enter -ScriptBlock {
            param($key, $arg)

            $line = ''
            $cursor = 0
            try {
                [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cursor)
                $converted = Convert-NebulaBareEnvAssignment $line
                if (-not $converted) { $converted = Convert-NebulaBareCd $line }
                if ($converted) {
                    try {
                        [Microsoft.PowerShell.PSConsoleReadLine]::Replace(0, $line.Length, $converted)
                    } catch {
                        try {
                            [Microsoft.PowerShell.PSConsoleReadLine]::Replace(0, $line.Length, $converted, $null, $null)
                        } catch {}
                    }
                }
            } catch {}

            [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine($key, $arg)
        } -ErrorAction SilentlyContinue

        # Nu/Reedline-style editing muscle memory: Ctrl+U removes everything
        # from the cursor back to the command start. At the line end this clears
        # the whole command in one chord, matching the expected shell UX.
        Set-PSReadLineKeyHandler -Key Ctrl+u -Function BackwardDeleteLine -ErrorAction SilentlyContinue
        Set-PSReadLineKeyHandler -Key Ctrl+k -Function ForwardDeleteLine -ErrorAction SilentlyContinue
        # gpui keymap 在传统 VT 路径把 Ctrl+Backspace 编成 \x17（Ctrl+W）：
        # 绑成 BackwardKillWord 才能让物理按键落到一次词删除。
        Set-PSReadLineKeyHandler -Key Ctrl+w -Function BackwardKillWord -ErrorAction SilentlyContinue
    } catch {}

    # OSC 133;C — wrap PSConsoleHostReadLine (the shell integration protocol's
    # approach, same signal nushell emits natively before executing): the host
    # only returns from ReadLine once it has a *complete* command, so C fires
    # exactly once, right before execution. The previous Enter-key-handler
    # emission misfired on multiline continuations (`{` + Enter) and blank
    # Enters, spinning Nebula's sidebar spinner for commands that never ran.
    # Defined after the Set-PSReadLineOption calls above so PSReadLine is
    # already imported and this global override sticks.
    if (-not $global:NebulaReadLineInstalled) {
        $existingReadLine = Get-Command PSConsoleHostReadLine -CommandType Function -ErrorAction SilentlyContinue
        $global:NebulaPreviousPSConsoleHostReadLine = if ($existingReadLine) {
            $existingReadLine.ScriptBlock
        } else {
            $null
        }
        $global:NebulaReadLineInstalled = $true
    }

    function global:PSConsoleHostReadLine {
        # 会话里再 source 一次 $PROFILE（或手动跑 oh-my-posh init）会用它们的
        # prompt 覆盖 Nebula 的 wrapper：OSC 133;A/D 从此不再发出，宿主侧的命令
        # 状态、耗时与历史定位就此静默失效。ReadLine 仍然是我们的，所以在这里把
        # prompt 包回去，并把刚出现的用户提示符记成视觉所有者。
        try {
            $installed = Get-Command prompt -CommandType Function -ErrorAction SilentlyContinue
            if ($global:NebulaPromptScriptBlock -and $installed -and
                -not $installed.ScriptBlock.ToString().Contains('NebulaPreviousPrompt')) {
                Install-NebulaPrompt $installed.ScriptBlock (Test-NebulaUserPrompt $installed.ScriptBlock)
            }
        } catch {}

        $line = $null
        if ($global:NebulaPreviousPSConsoleHostReadLine -and -not $global:NebulaPreviousReadLineRunning) {
            $global:NebulaPreviousReadLineRunning = $true
            try {
                $previousResult = @(& $global:NebulaPreviousPSConsoleHostReadLine)
                $line = if ($previousResult.Count -gt 0) {
                    [string]$previousResult[-1]
                } else {
                    ''
                }
            } catch {
                $line = $null
            } finally {
                $global:NebulaPreviousReadLineRunning = $false
            }
        }
        if ($null -eq $line) {
            $line = [Microsoft.PowerShell.PSConsoleReadLine]::ReadLine($Host.Runspace, $ExecutionContext)
        }
        # A blank Enter re-renders the prompt without running anything: no C,
        # so the spinner doesn't flash for a no-op.
        if (-not [string]::IsNullOrWhiteSpace($line)) {
            # The accepted PSReadLine text also sees recall, paste and native
            # completion. Resolve a simple PowerShell alias for scope tracking.
            $scopeLine = $line
            try {
                $first = ($line.Trim() -split '\s+', 2)[0]
                $alias = Get-Alias -Name $first -ErrorAction SilentlyContinue
                if ($alias -and $alias.ResolvedCommand) {
                    $scopeLine = $alias.ResolvedCommand.Name + ' ' + $line.Trim().Substring($first.Length)
                }
            } catch {}
            $owner = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($global:PebrelShellToken))
            $input64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($owner + "`n" + $line))
            [Console]::Write("$([char]27)]1337;SetUserVar=pebrel_input=$input64$([char]7)")
            $scopeLine64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($owner + "`n" + $scopeLine))
            [Console]::Write("$([char]27)]1337;SetUserVar=pebrel_command=$scopeLine64$([char]7)")
            [Console]::Write("$([char]27)]133;C$([char]7)")
        }
        $line
    }
}
Clear-Host
# Use the application's actual arguments after PowerShell has expanded variables
# and arrays. Console writes stay outside PowerShell's success-output pipeline.
function global:Invoke-PebrelConnection {
    param([string]$Program, [object[]]$Arguments)
    $application = Get-Command -Name $Program -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $token = $global:PebrelShellToken
    $owner = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($token))
    $words = @($Program) + @($Arguments | ForEach-Object { [string]$_ })
    $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($owner + "`n" + ($words -join [char]0)))
    [Console]::Write("$([char]27)]1337;SetUserVar=pebrel_shell=$token$([char]7)")
    [Console]::Write("$([char]27)]1337;SetUserVar=pebrel_connection=$payload$([char]7)")
    try {
        & $application @Arguments
    } finally {
        [Console]::Write("$([char]27)]1337;SetUserVar=pebrel_shell=$token$([char]7)")
    }
}

# A user's function/alias keeps precedence. Fully qualified executable paths
# also remain available as an explicit way to invoke the application directly.
if ((Get-Command ssh -ErrorAction SilentlyContinue).CommandType -eq 'Application') {
    function global:ssh { Invoke-PebrelConnection 'ssh' $args }
}
if ((Get-Command ssh.exe -ErrorAction SilentlyContinue).CommandType -eq 'Application') {
    function global:ssh.exe { Invoke-PebrelConnection 'ssh' $args }
}
if ((Get-Command wsl -ErrorAction SilentlyContinue).CommandType -eq 'Application') {
    function global:wsl { Invoke-PebrelConnection 'wsl' $args }
}
if ((Get-Command wsl.exe -ErrorAction SilentlyContinue).CommandType -eq 'Application') {
    function global:wsl.exe { Invoke-PebrelConnection 'wsl' $args }
}
# Query the native editor without changing its buffer or prediction settings.
if (Get-Command Set-PSReadLineKeyHandler -ErrorAction SilentlyContinue) {
    try {
        # Legacy ConPTY does not translate xterm F24; the modified F12 sequence
        # works with both native console and VT input, including the in-box host.
        $query = Get-Command Get-PSReadLineKeyHandler -ErrorAction Stop
        $existing = if ($query.Parameters.ContainsKey('Chord')) {
            Get-PSReadLineKeyHandler -Chord Ctrl+Shift+F12 -ErrorAction SilentlyContinue
        } else {
            # Windows PowerShell's bundled PSReadLine lacks -Chord.
            Get-PSReadLineKeyHandler | Where-Object { $_.Key -in 'Ctrl+Shift+F12','Shift+Ctrl+F12' }
        }
        if (-not $existing -or $existing.Function -eq 'Unbound') {
            Set-PSReadLineKeyHandler -Chord Ctrl+Shift+F12 -ScriptBlock {
                param($key, $arg)
                $line = ''
                $cursor = 0
                [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cursor)
                if ($line.Length -le 4096) {
                    $owner = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($global:PebrelShellToken))
                    $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($owner + "`nutf16`n" + $cursor + "`n" + $line))
                    [Console]::Write("$([char]27)]1337;SetUserVar=pebrel_editor=$payload$([char]7)")
                }
            }
            # PSReadLine 2.0 loses surrogate pairs inserted after native editing.
            # Advertise that boundary so completion cannot silently alter a name.
            $owner = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($global:PebrelShellToken))
            $capability = if ((Get-Module PSReadLine).Version -lt [Version]'2.1.0') { 'basic' } else { 'unicode' }
            $global:PebrelEditorReady = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($owner + "`n" + $capability))
            $global:PebrelCompletionInputReady = $true
        }
    } catch { $global:PebrelCompletionInputReady = $false }
}
