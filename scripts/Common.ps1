$ErrorActionPreference = 'Stop'
$script:Root = Split-Path $PSScriptRoot -Parent
$script:Runtime = Join-Path $script:Root 'runtime'
$script:ConfigFile = Join-Path $script:Runtime 'config.json'
$script:Utf8 = New-Object System.Text.UTF8Encoding($false)
# Child-process stdin writers inherit Console.InputEncoding; a UTF-8 console would prefix a BOM the guest agent rejects.
try { [Console]::InputEncoding = $script:Utf8 } catch { }

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, $script:Utf8)
}

function Quote-Argument([string]$Value) {
    # Windows CommandLineToArgvW quoting, including trailing backslashes.
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    '"' + ([regex]::Replace(([regex]::Replace($Value, '(\\*)"', '$1$1\"')), '(\\+)$', '$1$1')) + '"'
}

function Invoke-Native {
    param([string]$File, [string[]]$Arguments, [string]$InputText = '', [int]$TimeoutSeconds = 120,
          [scriptblock]$Poll = $null)
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $File
    $info.WorkingDirectory = $script:Root
    $info.Arguments = (($Arguments | ForEach-Object { Quote-Argument $_ }) -join ' ')
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.RedirectStandardInput = $true
    $info.StandardOutputEncoding = $script:Utf8
    $info.StandardErrorEncoding = $script:Utf8
    $p = New-Object Diagnostics.Process
    $p.StartInfo = $info
    $null = $p.Start()
    $stdout = $p.StandardOutput.ReadToEndAsync()
    $stderr = $p.StandardError.ReadToEndAsync()
    if ($InputText) { $p.StandardInput.WriteLine($InputText) }
    $p.StandardInput.Close()
    # Poll runs about once a second while the command is still working (progress reporting).
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not $p.WaitForExit(1000)) {
        if ((Get-Date) -gt $deadline) {
            $p.Kill()
            throw "Command timed out: $File"
        }
        if ($Poll) { try { & $Poll } catch { } }
    }
    $output = $stdout.Result
    $errorText = $stderr.Result
    $exitCode = $p.ExitCode
    $p.Dispose()
    if ($exitCode -ne 0) { throw "$File exited $exitCode`n$errorText`n$output" }
    return $output.Trim()
}

function Get-VBox {
    $installed = Join-Path $env:ProgramFiles 'Oracle\VirtualBox\VBoxManage.exe'
    if (Test-Path -LiteralPath $installed) { return $installed }
    throw 'VirtualBox is not installed. Run scripts\Install-Host.ps1 first.'
}

function Read-Config {
    if (-not (Test-Path -LiteralPath $script:ConfigFile)) { throw 'Run scripts\Prepare-VM.ps1 first.' }
    Get-Content -Raw -Encoding UTF8 -LiteralPath $script:ConfigFile | ConvertFrom-Json
}

function Invoke-VBox([string[]]$Arguments) {
    Invoke-Native -File (Get-VBox) -Arguments $Arguments
}

function Get-SshArguments($Config) {
    @('-i', (Join-Path $script:Runtime 'id_ed25519'),
      '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=4',
      '-o', 'StrictHostKeyChecking=accept-new',
      '-o', 'UserKnownHostsFile=runtime/known_hosts',
      '-o', 'IdentitiesOnly=yes', '-o', 'LogLevel=ERROR')
}

function Invoke-Guest($Config, $Request, [int]$TimeoutSeconds = 120, [scriptblock]$Poll = $null) {
    $arguments = @(Get-SshArguments $Config) + @('-p', [string]$Config.sshPort,
        'usbproxy@127.0.0.1', 'sudo -n /usr/local/sbin/usbproxy-agent')
    try {
        $raw = Invoke-Native -File 'ssh.exe' -Arguments $arguments -InputText ($Request | ConvertTo-Json -Depth 8 -Compress) -TimeoutSeconds $TimeoutSeconds -Poll $Poll
    } catch {
        # The agent exits non-zero with a JSON error on stdout; surface just its message.
        $line = @($_.Exception.Message -split "`n" | Where-Object { $_ -match '^\s*\{"ok": false' })
        if ($line.Count) { throw ($line[-1] | ConvertFrom-Json).error }
        throw
    }
    $response = $raw | ConvertFrom-Json
    if (-not $response.ok) { throw $response.error }
    return $response.data
}
