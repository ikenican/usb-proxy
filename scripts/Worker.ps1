param([Parameter(Mandatory=$true)][string]$RequestFile,
      [Parameter(Mandatory=$true)][string]$ResponseFile)
. "$PSScriptRoot\Common.ps1"

function Save-Config($Config) {
    Write-Utf8 $script:ConfigFile ($Config | ConvertTo-Json -Depth 8)
}

function Get-UsbDevices {
    param([switch]$IncludeAll)
    $raw = Invoke-VBox @('list','usbhost')
    $storageNodes = @(Get-CimInstance Win32_PnPEntity | Where-Object {
        $_.PNPDeviceID -match '^USB\\VID_' -and
        @($_.CompatibleID | Where-Object { $_ -match '^USB\\Class_08' }).Count -gt 0
    })
    $result = @()
    foreach ($block in ($raw -split '(?:\r?\n){2,}')) {
        if ($block -notmatch '(?m)^UUID:\s*(\S+)') { continue }
        $item = [ordered]@{uuid=$Matches[1]}
        foreach ($field in @('VendorId','ProductId','Manufacturer','Product','SerialNumber','Address','Port','Current State')) {
            if ($block -match ('(?m)^' + [regex]::Escape($field) + ':\s*(.*)$')) { $item[$field] = $Matches[1].Trim() }
            else { $item[$field] = '' }
        }
        if ($IncludeAll) {$result += [pscustomobject]$item; continue}
        if ($item['VendorId'] -notmatch '^0x([0-9a-fA-F]{4})') {continue}
        $vendor=$Matches[1]
        if ($item['ProductId'] -notmatch '^0x([0-9a-fA-F]{4})') {continue}
        $product=$Matches[1]
        $matching=@($storageNodes | Where-Object { $_.PNPDeviceID -match ('^USB\\VID_' + $vendor + '&PID_' + $product + '(?:&|\\)') })
        if ($matching.Count -eq 0) {continue}
        if ($matching.Count -gt 1 -and $item['SerialNumber']) {
            $serialMatches=@($matching | Where-Object {($_.PNPDeviceID -split '\\')[-1] -eq $item['SerialNumber']})
            if ($serialMatches.Count -eq 1) {$matching=$serialMatches}
        }
        $item['location']=''
        if ($matching.Count -eq 1) {
            $paths=Get-PnpDeviceProperty -InstanceId $matching[0].PNPDeviceID -KeyName 'DEVPKEY_Device_LocationPaths' -ErrorAction SilentlyContinue
            if ($paths -and $paths.Data) {$item['location']=[string]@($paths.Data)[0]}
        }
        $result += [pscustomobject]$item
    }
    return $result
}

function Get-Info($Config) {
    $raw = Invoke-VBox @('showvminfo',$Config.vmName,'--machinereadable')
    $state = 'unknown'
    if ($raw -match '(?m)^VMState="([^"]+)"') { $state = $Matches[1] }
    if ($state -eq 'running' -and $Config.shutdownRequestedAt) {$state='stopping'}
    return @{state=$state; attachedUsb=$Config.attachedUsb; attachedName=$Config.attachedName; vmName=$Config.vmName}
}

function Get-GuestStatus($Config) {
    Invoke-Guest $Config @{action='status'}
}

$script:SerialLog = Join-Path $script:Root 'logs\vm-serial.log'

# The UI polls this file next to the response for long operations.
function Write-Progress-File([string]$Phase) {
    try { Write-Utf8 ($ResponseFile + '.progress') (@{phase=$Phase; at=(Get-Date).ToString('o')} | ConvertTo-Json) } catch { }
}

function Get-SerialLogSize {
    if (Test-Path -LiteralPath $script:SerialLog) { return (Get-Item -LiteralPath $script:SerialLog).Length }
    return 0L
}

function Start-GuestVm($Config) {
    # Capture the boot console to a file so stalls can be detected and diagnosed.
    $raw = Invoke-VBox @('showvminfo',$Config.vmName,'--machinereadable')
    if (-not $raw.Contains('uartmode1="file,' + $script:SerialLog + '"')) {
        $null = Invoke-VBox @('modifyvm',$Config.vmName,'--uart1','0x3F8','4','--uart-mode1','file',$script:SerialLog)
    }
    # With Hyper-V active, VirtualBox runs through NEM where SMP guests intermittently stall at boot.
    if ($raw -notmatch '(?m)^cpus=1\s*$') { $null = Invoke-VBox @('modifyvm',$Config.vmName,'--cpus','1') }
    $null = Invoke-VBox @('startvm',$Config.vmName,'--type','headless')
}

# The provisioned Linux disk is sealed as an immutable image: VirtualBox discards every change
# at each VM start, so the guest always boots from the same clean state and never grows.
function Test-DiskSealed($Config) {
    return [bool]((Invoke-VBox @('showmediuminfo','disk',[string]$Config.disk)) -match '(?m)^Type:\s+immutable')
}

function Set-DiskType($Config,[string]$Type) {
    $vm = $Config.vmName
    $null = Invoke-VBox @('storageattach',$vm,'--storagectl','SATA','--port','0','--device','0','--medium','none')
    # Remove the differencing image VirtualBox keeps for an immutable disk before changing its type.
    $info = Invoke-VBox @('showmediuminfo','disk',[string]$Config.disk)
    $uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'
    $block = [regex]::Match($info,"(?m)^Child UUIDs:\s*$uuid(?:\r?\n\s+$uuid)*")
    foreach ($child in [regex]::Matches($block.Value,$uuid) | ForEach-Object Value) {
        try { $null = Invoke-VBox @('closemedium','disk',$child,'--delete') } catch { }
    }
    $null = Invoke-VBox @('modifymedium','disk',[string]$Config.disk,'--type',$Type)
    $null = Invoke-VBox @('storageattach',$vm,'--storagectl','SATA','--port','0','--device','0','--type','hdd','--medium',[string]$Config.disk)
}

function Stop-GuestVm($Config) {
    try { $null = Invoke-Guest $Config @{action='shutdown'} } catch { }
    try { $null = Invoke-VBox @('controlvm',$Config.vmName,'acpipowerbutton') } catch { }
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Info $Config).state -ne 'poweroff') {
        if ((Get-Date) -gt $deadline) { $null = Invoke-VBox @('controlvm',$Config.vmName,'poweroff'); Start-Sleep -Seconds 2; break }
        Start-Sleep -Seconds 2
    }
    $Config | Add-Member -NotePropertyName shutdownRequestedAt -NotePropertyValue $null -Force
    Save-Config $Config
}

function Wait-GuestReady($Config,[int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        try { if ((Get-GuestStatus $Config).ready) { return $true } } catch { }
        Start-Sleep -Seconds 3
    }
    return $false
}

# Keeps the guest agent in step with guest\usbproxy-agent.py after an application update.
function Get-LocalAgentHash {
    (Get-FileHash -LiteralPath (Join-Path $script:Root 'guest\usbproxy-agent.py') -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Sync-GuestAgent($Config,[switch]$NoReseed) {
    $source = Join-Path $script:Root 'guest\usbproxy-agent.py'
    $expected = Get-LocalAgentHash
    $hashArgs = @(Get-SshArguments $Config) + @('-p',[string]$Config.sshPort,'usbproxy@127.0.0.1','sha256sum /usr/local/sbin/usbproxy-agent')
    if (((Invoke-Native 'ssh.exe' $hashArgs) -split '\s+')[0] -eq $expected) {
        if ($Config.agentHash -ne $expected) {
            $Config | Add-Member -NotePropertyName agentHash -NotePropertyValue $expected -Force
            Save-Config $Config
        }
        return $false
    }
    Write-Progress-File 'updating'
    $staged = '/home/usbproxy/incoming/agent-' + [guid]::NewGuid().ToString('N') + '.py'
    $scpArgs = @(Get-SshArguments $Config) + @('-P',[string]$Config.sshPort,$source,('usbproxy@127.0.0.1:' + $staged))
    $null = Invoke-Native 'scp.exe' $scpArgs
    try {
        $null = Invoke-Guest $Config @{action='self-update'; source=$staged; sha256=$expected}
    } catch {
        if ($_.Exception.Message -notmatch 'Unknown action') { throw }
        if ($NoReseed) { return $false }
        # Agents older than self-update are replaced by re-running cloud-init with a fresh seed.
        # The disk is unsealed so the update persists; the caller seals it again afterwards.
        Stop-GuestVm $Config
        if (Test-DiskSealed $Config) { Set-DiskType $Config 'normal' }
        $null = & (Join-Path $PSScriptRoot 'New-GuestSeed.ps1')
        Start-GuestVm $Config
        if (-not (Wait-GuestReady $Config 600)) { throw 'Linux did not come back after updating the guest agent.' }
    } finally {
        try {
            $cleanupArgs = @(Get-SshArguments $Config) + @('-p',[string]$Config.sshPort,'usbproxy@127.0.0.1',('rm -f -- ' + $staged))
            $null = Invoke-Native 'ssh.exe' $cleanupArgs
        } catch { }
    }
    if (((Invoke-Native 'ssh.exe' $hashArgs) -split '\s+')[0] -ne $expected) { throw 'Guest agent update could not be verified.' }
    $Config | Add-Member -NotePropertyName agentHash -NotePropertyValue $expected -Force
    Save-Config $Config
    return $true
}

# Transfer progress for the UI: stage, current file, and weighted units done/total.
$script:Transfer = @{stage=''; file=''; index=0; count=0; done=0L; total=0L; last=[datetime]::MinValue}

function Start-Transfer([long]$Total,[int]$Count) {
    $script:Transfer.total = [Math]::Max(1L,$Total); $script:Transfer.count = $Count; $script:Transfer.done = 0L
}

function Report-Transfer([string]$Stage,[string]$File,[int]$Index,[long]$Add,[switch]$Force) {
    $t = $script:Transfer
    $t.stage = $Stage; $t.file = $File; $t.index = $Index; $t.done += $Add
    if (-not $Force -and ((Get-Date) - $t.last).TotalMilliseconds -lt 250) { return }
    $t.last = Get-Date
    try {
        Write-Utf8 ($ResponseFile + '.progress') (@{stage=$t.stage; file=$t.file; index=$t.index; count=$t.count
            done=[Math]::Min($t.done,$t.total); total=$t.total} | ConvertTo-Json -Compress)
    } catch { }
}

# Files to copy with their destination paths; folders keep their own name as the top level.
function Get-CopyPlan($Request) {
    $plan = New-Object 'System.Collections.Generic.List[object]'
    $names = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in @($Request.paths)) {
        $item = Get-Item -LiteralPath $path -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Symbolic links and junctions are not supported.' }
        if ($item.PSIsContainer) {
            # Enumerate explicitly so a junction cannot escape the selected directory.
            $queue = New-Object 'System.Collections.Generic.Queue[System.IO.DirectoryInfo]'
            $queue.Enqueue($item)
            $base = $item.Parent.FullName.TrimEnd('\') + '\'
            while ($queue.Count) {
                $directory = $queue.Dequeue()
                foreach ($child in @(Get-ChildItem -LiteralPath $directory.FullName -Force)) {
                    if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Symbolic link/junction found: ' + $child.FullName) }
                    if ($child.PSIsContainer) { $queue.Enqueue($child); continue }
                    $relative = $child.FullName.Substring($base.Length).Replace('\','/')
                    if (-not $names.Add($relative)) { throw "Duplicate destination: $relative" }
                    $plan.Add(@{Source=$child; Relative=$relative})
                }
            }
        } else {
            # Write-back of an opened file keeps its original name on the USB volume.
            $relative = if ($Request.targetName) {[string]$Request.targetName} else {$item.Name}
            if (-not $names.Add($relative)) { throw "Duplicate destination: $relative" }
            $plan.Add(@{Source=$item; Relative=$relative})
        }
    }
    return $plan
}

function New-TarHeader([string]$Name,[long]$Size) {
    $header = New-Object byte[] 512
    $ascii = [Text.Encoding]::ASCII
    $ascii.GetBytes($Name).CopyTo($header,0)
    $ascii.GetBytes('0000644').CopyTo($header,100)
    $ascii.GetBytes('0000000').CopyTo($header,108)
    $ascii.GetBytes('0000000').CopyTo($header,116)
    if ($Size -lt 8589934592L) {
        $ascii.GetBytes([Convert]::ToString($Size,8).PadLeft(11,'0')).CopyTo($header,124)
    } else {
        # GNU base-256 size field for files of 8 GiB or more.
        $header[124] = 0x80
        for ($i = 0; $i -lt 8; $i++) { $header[135 - $i] = [byte](($Size -shr (8 * $i)) -band 0xFF) }
    }
    $mtime = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $ascii.GetBytes([Convert]::ToString($mtime,8).PadLeft(11,'0')).CopyTo($header,136)
    for ($i = 148; $i -lt 156; $i++) { $header[$i] = 0x20 }
    $header[156] = [byte][char]'0'
    $ascii.GetBytes('ustar').CopyTo($header,257)
    $ascii.GetBytes('00').CopyTo($header,263)
    $sum = 0
    foreach ($b in $header) { $sum += $b }
    $ascii.GetBytes([Convert]::ToString($sum,8).PadLeft(6,'0')).CopyTo($header,148)
    $header[154] = 0; $header[155] = 0x20
    return ,$header
}

function Start-SshProcess($Config,[string]$Command) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = 'ssh.exe'
    $info.WorkingDirectory = $script:Root
    $info.Arguments = ((@(Get-SshArguments $Config) + @('-p',[string]$Config.sshPort,'usbproxy@127.0.0.1',$Command)) | ForEach-Object { Quote-Argument $_ }) -join ' '
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardErrorEncoding = $script:Utf8
    $p = New-Object Diagnostics.Process
    $p.StartInfo = $info
    $null = $p.Start()
    return $p
}

# Streams the files as a tar archive into the guest spool, hashing each one on the way.
function Send-Tar($Config,[string]$JobId,$Plan) {
    $remote = '/home/usbproxy/incoming/' + $JobId
    $p = Start-SshProcess $Config ('mkdir -p -- ' + $remote + ' && tar -x --no-same-owner --no-same-permissions -f - -C ' + $remote)
    $stderr = $p.StandardError.ReadToEndAsync()
    $stdout = $p.StandardOutput.ReadToEndAsync()
    $out = $p.StandardInput.BaseStream
    $buffer = New-Object byte[] (1MB)
    $zeros = New-Object byte[] 1024
    $files = New-Object 'System.Collections.Generic.List[object]'
    try {
        for ($i = 0; $i -lt $Plan.Count; $i++) {
            $item = $Plan[$i]
            $size = [long]$item.Source.Length
            $blob = 'file-' + $i.ToString('D8')
            $header = New-TarHeader $blob $size
            $out.Write($header,0,512)
            $sha = [Security.Cryptography.SHA256]::Create()
            $in = [IO.File]::Open($item.Source.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            try {
                Report-Transfer 'upload' $item.Relative ($i + 1) 0 -Force
                $remaining = $size
                while ($remaining -gt 0) {
                    $n = $in.Read($buffer,0,[int][Math]::Min([long]$buffer.Length,$remaining))
                    if ($n -le 0) { throw ('File changed while copying: ' + $item.Source.FullName) }
                    $null = $sha.TransformBlock($buffer,0,$n,$null,0)
                    $out.Write($buffer,0,$n)
                    $remaining -= $n
                    Report-Transfer 'upload' $item.Relative ($i + 1) $n
                }
                if ($in.ReadByte() -ne -1) { throw ('File changed while copying: ' + $item.Source.FullName) }
            } finally { $in.Dispose() }
            $null = $sha.TransformFinalBlock($buffer,0,0)
            $hash = ([BitConverter]::ToString($sha.Hash) -replace '-','').ToLowerInvariant()
            $sha.Dispose()
            $pad = (512 - ($size % 512)) % 512
            if ($pad) { $out.Write($zeros,0,$pad) }
            $files.Add(@{relative=$item.Relative; blob=$blob; size=$size; sha256=$hash})
        }
        $manifest = $script:Utf8.GetBytes((@{files=@($files.ToArray())} | ConvertTo-Json -Depth 7))
        $header = New-TarHeader 'manifest.json' $manifest.Length
        $out.Write($header,0,512)
        $out.Write($manifest,0,$manifest.Length)
        $pad = (512 - ($manifest.Length % 512)) % 512
        if ($pad) { $out.Write($zeros,0,$pad) }
        $out.Write($zeros,0,1024)
        $out.Close()
    } catch {
        $reason = $_.Exception.Message
        try { $p.Kill() } catch { }
        $detail = ''
        try { $detail = $stderr.Result } catch { }
        throw ('Upload to Linux failed: ' + $reason + ' ' + $detail)
    }
    if (-not $p.WaitForExit(600000)) { $p.Kill(); throw 'Upload to Linux timed out.' }
    $exitCode = $p.ExitCode
    $errorText = $stderr.Result
    $p.Dispose()
    if ($exitCode -ne 0) { throw ('Upload to Linux failed: ' + $errorText) }
    return $files
}

# Receives one file streamed by the agent; its JSON result arrives on stderr.
function Receive-Stream($Config,$Request,[string]$Local,[string]$Label) {
    $p = Start-SshProcess $Config 'sudo -n /usr/local/sbin/usbproxy-agent'
    $stderr = $p.StandardError.ReadToEndAsync()
    $p.StandardInput.WriteLine(($Request | ConvertTo-Json -Compress))
    $p.StandardInput.Close()
    $in = $p.StandardOutput.BaseStream
    $buffer = New-Object byte[] (1MB)
    $sha = [Security.Cryptography.SHA256]::Create()
    $size = 0L
    $file = [IO.File]::Open($Local,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {
        Report-Transfer 'download' $Label 1 0 -Force
        while (($n = $in.Read($buffer,0,$buffer.Length)) -gt 0) {
            $null = $sha.TransformBlock($buffer,0,$n,$null,0)
            $file.Write($buffer,0,$n)
            $size += $n
            Report-Transfer 'download' $Label 1 $n
        }
    } finally { $file.Dispose() }
    $null = $sha.TransformFinalBlock($buffer,0,0)
    $hash = ([BitConverter]::ToString($sha.Hash) -replace '-','').ToLowerInvariant()
    $sha.Dispose()
    $p.WaitForExit()
    $exitCode = $p.ExitCode
    $lines = @($stderr.Result -split "`n" | Where-Object { $_ -match '^\s*\{' })
    $p.Dispose()
    if (-not $lines.Count) { throw ('Linux did not return a result (exit ' + $exitCode + ').') }
    $response = $lines[-1] | ConvertFrom-Json
    if (-not $response.ok) { throw $response.error }
    if ($exitCode -ne 0) { throw ('Linux returned exit ' + $exitCode) }
    if ($size -ne [long]$response.data.size -or $hash -ne $response.data.sha256) {
        throw 'Downloaded file failed SHA-256 verification.'
    }
    return $response.data
}

function Invoke-Eject($Config) {
    # A stopping or powered-off guest has already unmounted; only release the device.
    if ((Get-Info $Config).state -eq 'running') { $null = Invoke-Guest $Config @{action='eject'} }
    if ($Config.attachedUsb) {
        try {
            $null = Invoke-VBox @('controlvm',$Config.vmName,'usbdetach',[string]$Config.attachedUsb)
        } catch {
            $remaining=@(Get-UsbDevices -IncludeAll | Where-Object {$_.uuid -eq $Config.attachedUsb -and $_.'Current State' -eq 'Captured'})
            if ($remaining.Count) {throw}
            # An unplugged/already released device needs only local state cleanup.
        }
        $Config.attachedUsb = $null
        $Config | Add-Member -NotePropertyName attachedName -NotePropertyValue $null -Force
        Save-Config $Config
    }
}

function Invoke-Operation($request) {
    $config = Read-Config
    switch ($request.action) {
        'status' {
            $info = Get-Info $config
            $guest = $null
            $message = ''
            if ($info.state -eq 'running') {
                try { $guest = Get-GuestStatus $config } catch { $message = $_.Exception.Message }
                # An already-running guest picks up a newer agent without a restart.
                if ($guest -and $config.agentHash -ne (Get-LocalAgentHash)) {
                    try { $null = Sync-GuestAgent $config -NoReseed } catch { }
                }
            }
            return @{vm=$info; guest=$guest; message=$message; usb=@(Get-UsbDevices); preferredLocation=$config.preferredLocation}
        }
        'remember' {
            $location=[string]$request.location
            if (-not $location) {throw 'Windows did not provide an unambiguous physical port path for this device.'}
            $config | Add-Member -NotePropertyName preferredLocation -NotePropertyValue $location -Force
            Save-Config $config
            return @{location=$location}
        }
        'start' {
            $info = Get-Info $config
            $history = @($config.bootSeconds | Where-Object {$_})
            $booted = $false
            if ($info.state -eq 'poweroff') {
                $config | Add-Member -NotePropertyName shutdownRequestedAt -NotePropertyValue $null -Force
                Save-Config $config
                Start-GuestVm $config
                $booted = $true
            } elseif ($info.state -ne 'running') { throw "VM is $($info.state). Resolve this state before starting." }
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $deadline = (Get-Date).AddMinutes(8)
            # A boot that has completed before may stall silently under Hyper-V; the first boot installs packages.
            $stallSeconds = if ($history.Count) {90} else {240}
            $retried = $false
            $lastSize = -1L; $lastGrowth = Get-Date
            while ((Get-Date) -lt $deadline) {
                $isReady = $false
                try { $isReady = [bool](Get-GuestStatus $config).ready } catch { }
                if ($isReady) {
                    if ($booted) {
                        $config | Add-Member -NotePropertyName bootSeconds -NotePropertyValue (@($history + [int]$watch.Elapsed.TotalSeconds) | Select-Object -Last 5) -Force
                        Save-Config $config
                    }
                    $updated = Sync-GuestAgent $config
                    $sealed = $false
                    if (-not (Test-DiskSealed $config)) {
                        # First run after provisioning: freeze this clean state as the image every start resets to.
                        Write-Progress-File 'sealing'
                        Stop-GuestVm $config
                        Set-DiskType $config 'immutable'
                        Start-GuestVm $config
                        if (-not (Wait-GuestReady $config 300)) { throw 'Linux did not start from the sealed image.' }
                        $null = Sync-GuestAgent $config
                        $sealed = $true
                    }
                    return @{ready=$true; vmName=$config.vmName; retried=$retried; agentUpdated=$updated; sealed=$sealed}
                }
                $size = Get-SerialLogSize
                if ($size -ne $lastSize) { $lastSize = $size; $lastGrowth = Get-Date }
                elseif ($booted -and -not $retried -and ((Get-Date) - $lastGrowth).TotalSeconds -gt $stallSeconds) {
                    # Console output stopped and the guest never answered: power-cycle once.
                    Write-Progress-File 'retry'
                    $null = Invoke-VBox @('controlvm',$config.vmName,'poweroff')
                    Start-Sleep -Seconds 2
                    Start-GuestVm $config
                    $retried = $true
                    $watch.Restart()
                    $deadline = (Get-Date).AddMinutes(6)
                    $lastSize = -1L; $lastGrowth = Get-Date
                }
                Start-Sleep -Seconds 3
            }
            throw 'Linux is not ready after eight minutes. See logs\vm-serial.log for the boot console.'
        }
        'attach' {
            if ((Get-Info $config).state -ne 'running') { throw 'Start Linux first.' }
            if ($config.attachedUsb) { throw 'Eject the currently attached device first.' }
            $devices = @(Get-UsbDevices)
            $device = @($devices | Where-Object { $_.uuid -eq $request.uuid })
            if ($device.Count -ne 1) { throw 'USB device is no longer available. Refresh the list.' }
            if ($device[0].'Current State' -notin @('Available','Busy')) { throw 'USB device cannot currently be captured.' }
            # The UI requires explicit selection. No wildcard filter captures keyboards or other USB devices.
            $null = Invoke-VBox @('controlvm',$config.vmName,'usbattach',[string]$request.uuid)
            $config.attachedUsb = [string]$request.uuid
            $config | Add-Member -NotePropertyName attachedName -NotePropertyValue ([string]$request.name) -Force
            Save-Config $config
            # The guest needs a few seconds to enumerate the disk; wait for its partitions instead of a fixed delay.
            $deadline = (Get-Date).AddSeconds(20)
            $guest = $null
            do {
                Start-Sleep -Seconds 1
                try { $guest = Get-GuestStatus $config } catch { }
            } while ((-not $guest -or @($guest.volumes | Where-Object {$_}).Count -eq 0) -and (Get-Date) -lt $deadline)
            if (-not $guest) { $guest = Get-GuestStatus $config }
            return @{attached=$request.uuid; guest=$guest}
        }
        'copy' {
            if ((Get-Info $config).state -ne 'running') {throw 'Linux is not running or is shutting down.'}
            $guest = Get-GuestStatus $config
            $selected = @($guest.volumes | Where-Object { $_.path -eq $request.device })
            if ($selected.Count -ne 1) { throw 'Select a supported USB volume.' }
            $plan = @(Get-CopyPlan $request)
            if ($plan.Count -eq 0) { throw 'The selection contains no files. Empty directories are not copied.' }
            $total = 0L
            foreach ($item in $plan) { $total += $item.Source.Length }
            if ($selected[0].fstype -eq 'vfat' -and @($plan | Where-Object { $_.Source.Length -ge 4294967296 }).Count) {
                throw 'FAT32 cannot store a file of 4 GiB or larger.'
            }
            $freeArgs=@(Get-SshArguments $config) + @('-p',[string]$config.sshPort,'usbproxy@127.0.0.1','df -B1 --output=avail /home/usbproxy/incoming | tail -n 1')
            $free=[long](Invoke-Native 'ssh.exe' $freeArgs)
            if ($free -lt $total + 1MB) {throw 'Not enough staging space in the Linux VM. Copy a smaller batch.'}
            # Progress units: upload (1x) then the guest writes and reads back each byte (2x).
            Start-Transfer ($total * 3) $plan.Count
            $jobId = [guid]::NewGuid().ToString('N')
            $remote = '/home/usbproxy/incoming/' + $jobId
            try {
                $null = Send-Tar $config $jobId $plan
                $pollArgs = @(Get-SshArguments $config) + @('-p',[string]$config.sshPort,'usbproxy@127.0.0.1',('cat ' + $remote + '/.progress 2>/dev/null || true'))
                $poll = {
                    $text = Invoke-Native 'ssh.exe' $pollArgs
                    if ($text) {
                        $guestProgress = $text | ConvertFrom-Json
                        $script:Transfer.done = $total + [long]$guestProgress.done
                        Report-Transfer ([string]$guestProgress.stage) ([string]$guestProgress.file) ([int]$guestProgress.index) 0 -Force
                    }
                }
                return Invoke-Guest $config @{action='copy'; device=$request.device; spool=$remote
                    dest=[string]$request.dest; overwrite=[bool]$request.overwrite} -TimeoutSeconds 7200 -Poll $poll
            } finally {
                try {
                    # jobId is generated locally as lowercase GUID hex. Remove only this job's guest spool.
                    $cleanupArgs=@(Get-SshArguments $config) + @('-p',[string]$config.sshPort,'usbproxy@127.0.0.1',('rm -rf -- ' + $remote))
                    $null=Invoke-Native 'ssh.exe' $cleanupArgs
                } catch { }
            }
        }
        {$_ -in 'list','delete','mkdir','rename'} {
            if ((Get-Info $config).state -ne 'running') {throw 'Linux is not running or is shutting down.'}
            $guestRequest = @{action=$request.action; device=[string]$request.device}
            foreach ($key in 'path','name') { if ($null -ne $request.$key) { $guestRequest[$key] = [string]$request.$key } }
            if ($null -ne $request.paths) { $guestRequest['paths'] = @($request.paths | ForEach-Object {[string]$_}) }
            return Invoke-Guest $config $guestRequest -TimeoutSeconds 600
        }
        'fetch' {
            if ((Get-Info $config).state -ne 'running') {throw 'Linux is not running or is shutting down.'}
            # Names valid on Linux file systems may be invalid on Windows.
            $leaf = ([string]$request.path).Split('/')[-1]
            $name = [regex]::Replace($leaf,'[\/:*?"<>|\x00-\x1f]','_').TrimEnd(' ','.')
            if (-not $name) { $name = 'file' }
            $folder = Join-Path $script:Runtime ('open\' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $folder -Force | Out-Null
            $local = Join-Path $folder $name
            Start-Transfer ([long]$request.size) 1
            try {
                $result = Receive-Stream $config @{action='stream'; device=[string]$request.device; path=[string]$request.path} $local $leaf
            } catch {
                Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
                throw
            }
            if ($request.saveTo) {
                Move-Item -LiteralPath $local -Destination ([string]$request.saveTo) -Force
                Remove-Item -LiteralPath $folder -Recurse -Force
                return @{saved=[string]$request.saveTo; path=$result.path}
            }
            return @{local=$local; path=$result.path; name=$result.name}
        }
        'eject' {
            Invoke-Eject $config
            return @{ejected=$true}
        }
        'shutdown' {
            # Safely eject first: unmount (sync) in the guest, then hand the device back to Windows.
            $ejected = [bool]$config.attachedUsb
            if ($ejected) { Invoke-Eject $config }
            if ((Get-Info $config).state -ne 'running') {return @{shutdown=$true; ejected=$ejected}}
            # The agent syncs, unmounts and schedules a fallback 'shutdown -h +1'.
            $null = Invoke-Guest $config @{action='shutdown'}
            $config | Add-Member -NotePropertyName shutdownRequestedAt -NotePropertyValue (Get-Date).ToString('o') -Force
            Save-Config $config
            # An ACPI power-button press makes systemd power off now instead of a minute later.
            try { $null = Invoke-VBox @('controlvm',$config.vmName,'acpipowerbutton') } catch { }
            $deadline = (Get-Date).AddSeconds(75)
            while ((Get-Date) -lt $deadline) {
                if ((Get-Info $config).state -eq 'poweroff') { return @{shutdown=$true; ejected=$ejected; off=$true} }
                Start-Sleep -Seconds 2
            }
            return @{shutdown=$true; ejected=$ejected; off=$false}
        }
        default { throw 'Unknown operation.' }
    }
}

try {
    $lock = [IO.File]::Open((Join-Path $script:Runtime 'operation.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $request = Get-Content -Raw -Encoding UTF8 -LiteralPath $RequestFile | ConvertFrom-Json
    $data = Invoke-Operation $request
    $result = @{ok=$true; data=$data}
} catch {
    $result = @{ok=$false; error=$_.Exception.Message}
    $logfile = Join-Path $script:Root 'logs\worker-errors.txt'
    Add-Content -LiteralPath $logfile -Value ((Get-Date).ToString('o') + "`n" + $_.ToString() + "`n" + $_.ScriptStackTrace) -Encoding UTF8
}
finally { if ($lock) {$lock.Dispose()} }
Write-Utf8 $ResponseFile ($result | ConvertTo-Json -Depth 12)
