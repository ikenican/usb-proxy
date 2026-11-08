param([switch]$SmokeTest)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -Path "$PSScriptRoot\AppleUi.cs" -ReferencedAssemblies System.Windows.Forms,System.Drawing
. "$PSScriptRoot\..\scripts\Common.ps1"
[UsbProxyUi.Native]::EnableDpiAwareness()
[Windows.Forms.Application]::EnableVisualStyles()
$script:busy = $false
$script:process = $null
$script:pendingAction = ''
$script:pendingRequest = $null
$script:followUp = $null
$script:usb = @()
$script:volumes = @()
$script:attached = @()
$script:ready = $false
$script:vmState = 'unknown'
$script:closeWhenDone = $false
$script:lastStoppingPoll = [datetime]::MinValue
$script:pendingNav = $null
$script:volumeInfo = @{}
$script:switchTarget = $null
$script:editAfter = $null
$script:refreshWanted = $false
$script:lastDeviceChange = [datetime]::MinValue
$script:openFiles = New-Object 'System.Collections.Generic.List[object]'
$script:lastOpenCheck = [datetime]::MinValue
$script:prompting = $false
$script:samples = New-Object 'System.Collections.Generic.List[object]'
$script:linuxClip = $null
$script:typeNames = @{}
$actionNames = @{status='重新整理'; start='啟動 Linux'; shutdown='關閉 Linux'; attach='切換到 Linux'; eject='退出隨身碟'
    copy='複製到隨身碟'; transfer='隨身碟間傳輸'; download='複製到電腦'; list='讀取資料夾'; delete='刪除'; mkdir='新增資料夾'
    rename='重新命名'; fetch='讀取檔案'; setup='安裝 Linux 環境'}
$script:setupOffered = $false
$quietActions = @('status','list')
$uiFile = Join-Path $script:Runtime 'ui.json'
$ui = @{zoom_win=0; zoom_lin=0; icon_win=2; icon_lin=2; width=1200; height=760; maximized=$false; split=0.5}
try { $saved = Get-Content -Raw -Encoding UTF8 -LiteralPath $uiFile | ConvertFrom-Json; foreach ($key in @($ui.Keys)) { if ($null -ne $saved.$key) { $ui[$key] = $saved.$key } } } catch { }

$fontFamily = 'Microsoft JhengHei UI'
$bodyFont = New-Object Drawing.Font($fontFamily,9.75)
$headlineFont = New-Object Drawing.Font($fontFamily,11,[Drawing.FontStyle]::Bold)
$captionFont = New-Object Drawing.Font($fontFamily,9)
$glyphFont = New-Object Drawing.Font('Segoe UI Symbol',11)

$W = 1200; $H = 760
$form = New-Object Windows.Forms.Form
$form.AutoScaleDimensions = New-Object Drawing.SizeF(96,96)
$form.AutoScaleMode = 'Dpi'
$form.Text = 'USB Proxy'
$form.Icon = New-Object Drawing.Icon("$PSScriptRoot\app.ico")
$form.ClientSize = New-Object Drawing.Size($W,$H)
$form.MinimumSize = New-Object Drawing.Size(900,560)
$form.BackColor = [UsbProxyUi.Theme]::Background
$form.ForeColor = [UsbProxyUi.Theme]::Label
$form.Font = $bodyFont
$form.StartPosition = 'CenterScreen'
$form.Padding = New-Object Windows.Forms.Padding(16,12,16,6)
$penguin = [Drawing.Image]::FromFile("$PSScriptRoot\penguin.png")
$scale = $form.DeviceDpi / 96

function New-Text($Parent,$Text,$X,$Y,$Width,$Height=22,$Font=$bodyFont,$Color=[UsbProxyUi.Theme]::Label,$Anchor='Top,Left') {
    $label = New-Object Windows.Forms.Label
    $label.Text=$Text; $label.Font=$Font; $label.ForeColor=$Color
    $label.BackColor = if ($Parent -is [UsbProxyUi.Card]) {$Parent.FillColor} else {$Parent.BackColor}
    $label.AutoEllipsis=$true; $label.UseMnemonic=$false; $label.Anchor=$Anchor
    $label.Location=New-Object Drawing.Point($X,$Y); $label.Size=New-Object Drawing.Size($Width,$Height)
    $Parent.Controls.Add($label)
    return $label
}
function New-Button($Parent,$Text,$Kind,$X,$Y,$Width,$Handler,$Height=32,$Anchor='Top,Left') {
    $button = New-Object UsbProxyUi.PillButton
    $button.Text=$Text; $button.Kind=$Kind; $button.Anchor=$Anchor
    $button.Location=New-Object Drawing.Point($X,$Y); $button.Size=New-Object Drawing.Size($Width,$Height)
    $button.Add_Click($Handler)
    $Parent.Controls.Add($button)
    return $button
}
function New-Spacer($Dock,$Size) {
    $spacer = New-Object Windows.Forms.Panel
    $spacer.Dock = $Dock; $spacer.Height = $Size; $spacer.BackColor = $form.BackColor
    return $spacer
}

# --- Layout (docked, so the window can be resized and maximized) ---
$innerWidth = $W - 32

$split = New-Object Windows.Forms.SplitContainer
$split.Size = New-Object Drawing.Size($innerWidth,($H-12-64-10-36))
$split.SplitterWidth = 12
$split.BackColor = $form.BackColor
$split.Panel1.BackColor = $form.BackColor; $split.Panel2.BackColor = $form.BackColor
$split.SplitterDistance = [int](($innerWidth - 12) / 2)

$transfer = New-Object UsbProxyUi.Card
$transfer.Size = New-Object Drawing.Size($innerWidth,92)
$transferTitle = New-Text $transfer '' 16 10 ($innerWidth-32) 22 $headlineFont ([UsbProxyUi.Theme]::Label) 'Top,Left,Right'
$transferFile = New-Text $transfer '' 16 34 ($innerWidth-32) 20 $captionFont ([UsbProxyUi.Theme]::SecondaryLabel) 'Top,Left,Right'
$transferBar = New-Object UsbProxyUi.ThinProgress
$transferBar.Location = New-Object Drawing.Point(16,58); $transferBar.Size = New-Object Drawing.Size(($innerWidth-32),6); $transferBar.Anchor = 'Top,Left,Right'
$transfer.Controls.Add($transferBar)
$transferStats = New-Text $transfer '' 16 66 ($innerWidth-32) 20 $captionFont ([UsbProxyUi.Theme]::SecondaryLabel) 'Top,Left,Right'
$transfer.Dock = 'Bottom'; $transfer.Visible = $false
$transferGap = New-Spacer 'Bottom' 10; $transferGap.Visible = $false

$statusBar = New-Object Windows.Forms.Panel
$statusBar.Size = New-Object Drawing.Size($innerWidth,30); $statusBar.BackColor = $form.BackColor
$statusText = New-Text $statusBar '' 2 6 ($innerWidth-110) 22 $captionFont ([UsbProxyUi.Theme]::SecondaryLabel) 'Top,Left,Right'
$logButton = New-Button $statusBar '活動記錄' 'Plain' ($innerWidth-96) 1 96 { $logWindow.Show(); $logWindow.Activate() } 28 'Top,Right'
$statusBar.Dock = 'Bottom'

$linuxCard = New-Object UsbProxyUi.Card
$linuxCard.Size = New-Object Drawing.Size($innerWidth,64)
$dot = New-Object UsbProxyUi.PenguinBadge
$dot.Location = New-Object Drawing.Point(14,9); $dot.Size = New-Object Drawing.Size(38,44)
$linuxCard.Controls.Add($dot)
$linuxTitle = New-Text $linuxCard 'Linux' 62 9 500 22 $headlineFont
$linuxDetail = New-Text $linuxCard '正在檢查狀態…' 62 33 ($innerWidth-62-170) 20 $captionFont ([UsbProxyUi.Theme]::SecondaryLabel) 'Top,Left,Right'
$bootProgress = New-Object UsbProxyUi.ThinProgress
$bootProgress.Location = New-Object Drawing.Point(62,55); $bootProgress.Size = New-Object Drawing.Size(($innerWidth-62-170),5); $bootProgress.Anchor = 'Top,Left,Right'
$bootProgress.Visible = $false
$linuxCard.Controls.Add($bootProgress)
$linuxToggle = New-Button $linuxCard '啟動 Linux' 'Primary' ($innerWidth-14-128) 15 128 {
    if ($script:vmState -eq 'notinstalled') {Start-Setup; return}
    if ($script:vmState -eq 'running') {
        if ($script:attached.Count) {
            $choice = [Windows.Forms.MessageBox]::Show('關閉 Linux 前會先安全退出所有隨身碟並交還 Windows。請確認沒有正在進行的複製。','關閉 Linux',[Windows.Forms.MessageBoxButtons]::OKCancel,[Windows.Forms.MessageBoxIcon]::Information)
            if ($choice -ne [Windows.Forms.DialogResult]::OK) {return}
        }
        Begin-Operation @{action='shutdown'}
    } else { Begin-Operation @{action='start'} }
} 34 'Top,Right'
$linuxCard.Dock = 'Top'

# Docking order: the fill control is added first, edges after it.
$form.Controls.Add($split); $split.Dock = 'Fill'
$form.Controls.Add($transferGap); $form.Controls.Add($transfer); $form.Controls.Add($statusBar)
$form.Controls.Add((New-Spacer 'Top' 10)); $form.Controls.Add($linuxCard)

$logWindow = New-Object Windows.Forms.Form
$logWindow.Text = '活動記錄'; $logWindow.Icon = $form.Icon; $logWindow.ShowInTaskbar = $false
$logWindow.Size = New-Object Drawing.Size(640,360); $logWindow.StartPosition = 'CenterParent'; $logWindow.Owner = $form
$log = New-Object Windows.Forms.TextBox
$log.Multiline=$true; $log.ReadOnly=$true; $log.ScrollBars='Vertical'; $log.Dock='Fill'; $log.BorderStyle='None'
$log.BackColor=[UsbProxyUi.Theme]::Surface; $log.Font=$captionFont
$logWindow.Controls.Add($log)
$logWindow.Add_FormClosing({ param($s,$e) if ($e.CloseReason -eq 'UserClosing') { $e.Cancel=$true; $logWindow.Hide() } })

$smallIcons = New-Object Windows.Forms.ImageList; $smallIcons.ColorDepth = 'Depth32Bit'; $smallIcons.ImageSize = New-Object Drawing.Size([int](16*$scale),[int](16*$scale))

# A pane: header (back, up, address, view) above an Explorer-style list.
function New-Pane($Container,[string]$Key,[string]$Root) {
    $width = $Container.Width
    $body = New-Object UsbProxyUi.Card
    $body.Padding = New-Object Windows.Forms.Padding(6)
    $body.Dock = 'Fill'
    $list = New-Object UsbProxyUi.ExplorerListView
    $list.Dock = 'Fill'; $list.BackColor = [UsbProxyUi.Theme]::Surface; $list.Font = $bodyFont
    $large = New-Object Windows.Forms.ImageList; $large.ColorDepth = 'Depth32Bit'; $large.ImageSize = New-Object Drawing.Size([int](48*$scale),[int](48*$scale))
    $list.LargeImageList = $large; $list.SmallImageList = $smallIcons
    $body.Controls.Add($list)
    $empty = New-Object Windows.Forms.Label
    $empty.Dock = 'Fill'; $empty.TextAlign = 'MiddleCenter'; $empty.ForeColor = [UsbProxyUi.Theme]::SecondaryLabel
    $empty.BackColor = [UsbProxyUi.Theme]::Surface; $empty.AllowDrop = $true; $empty.Visible = $false
    $list.Controls.Add($empty)
    $header = New-Object Windows.Forms.Panel
    $header.Size = New-Object Drawing.Size($width,42); $header.BackColor = $form.BackColor
    $pane = @{Key=$Key; Root=$Root; List=$list; Empty=$empty; Large=$large; Sorter=$list.ListViewItemSorter
        Location=@{kind='pc'}; History=(New-Object 'System.Collections.Generic.List[object]'); Settling=$false}
    $pane.Back = New-Button $header '←' 'Secondary' 0 2 36 { Go-Back (Get-PaneOf $this) } 32
    $pane.Up = New-Button $header '↑' 'Secondary' 40 2 36 { Go-Up (Get-PaneOf $this) } 32
    $pane.Back.Font = $glyphFont; $pane.Up.Font = $glyphFont
    $address = New-Object UsbProxyUi.Card
    $address.Location = New-Object Drawing.Point(82,2); $address.Size = New-Object Drawing.Size(($width-82-122),32); $address.Anchor = 'Top,Left,Right'
    $header.Controls.Add($address)
    $pane.Address = New-Text $address $Root 12 6 ($width-82-122-24) 20 $bodyFont ([UsbProxyUi.Theme]::Label) 'Top,Left,Right'
    $view = New-Object UsbProxyUi.PopupSelect
    $view.Location = New-Object Drawing.Point(($width-114),2); $view.Size = New-Object Drawing.Size(114,32); $view.Anchor = 'Top,Right'
    $view.SetItems([string[]]@('大圖示','清單','詳細資料'))
    $header.Controls.Add($view)
    $pane.View = $view
    $header.Dock = 'Top'
    $Container.Controls.Add($body); $Container.Controls.Add($header)
    return $pane
}
$win = New-Pane $split.Panel1 'win' '本機'
$lin = New-Pane $split.Panel2 'lin' 'Linux'
$panes = @($win,$lin)
$script:active = $win
# Event handlers find their pane from the sending control.
function Get-PaneOf($Control) {
    foreach ($pane in $panes) {
        if ($Control -eq $pane.List -or $Control -eq $pane.Empty -or $Control -eq $pane.View -or $Control -eq $pane.Back -or $Control -eq $pane.Up) {return $pane}
    }
    return $null
}

function Log-Line([string]$Text) { $log.AppendText((Get-Date -Format 'HH:mm:ss') + '   ' + $Text + "`r`n") }
function Set-Message([string]$Text) { $statusText.Text = $Text }
function Format-Size([double]$Bytes) {
    if ($Bytes -lt 1KB) {return ('{0} 位元組' -f $Bytes)}
    if ($Bytes -lt 1MB) {return ('{0:N1} KB' -f ($Bytes/1KB))}
    if ($Bytes -lt 1GB) {return ('{0:N1} MB' -f ($Bytes/1MB))}
    return ('{0:N1} GB' -f ($Bytes/1GB))
}
function Format-Duration([int]$Seconds) {
    if ($Seconds -ge 3600) {return ('{0} 小時 {1} 分' -f [Math]::Floor($Seconds/3600),[Math]::Floor(($Seconds%3600)/60))}
    if ($Seconds -ge 60) {return ('{0} 分 {1:00} 秒' -f [Math]::Floor($Seconds/60),($Seconds%60))}
    return ('{0} 秒' -f $Seconds)
}
function Get-ParentPath([string]$Path) {
    $index = $Path.LastIndexOf('/')
    if ($index -lt 0) {return ''} else {return $Path.Substring(0,$index)}
}
function Join-RemotePath([string]$Folder,[string]$Name) { if ($Folder) {"$Folder/$Name"} else {$Name} }
function Get-Selected($Pane) { @($Pane.List.SelectedItems | ForEach-Object {$_.Tag}) }
function Get-VolumeLabel([string]$Device) {
    $v = @($script:volumes | Where-Object {$_.path -eq $Device})
    if ($v.Count -and $v[0].label) {return [string]$v[0].label}
    return 'USB 隨身碟'
}
# The attached drive a Linux volume belongs to (matched by the serial Linux reports).
function Get-DriveOf($Volume) {
    $match = @($script:attached | Where-Object { $_.serial -and $_.serial -eq $Volume.serial })
    if ($match.Count) {return $match[0]}
    if ($script:attached.Count -eq 1) {return $script:attached[0]}
    return $null
}
function Get-TypeName([string]$Name,[bool]$Folder) {
    $key = if ($Folder) {'~folder'} else {[IO.Path]::GetExtension($Name).ToLowerInvariant()}
    if (-not $script:typeNames.ContainsKey($key)) {$script:typeNames[$key] = [UsbProxyUi.ShellIcons]::TypeName($Name,$Folder)}
    return $script:typeNames[$key]
}

# Icons come from the Windows shell, cached per extension (or per path for drives and special folders).
function Get-IconKey($Entry,$Pane) {
    switch ($Entry.Kind) {
        'device' {$key='~device'}
        {$_ -in 'volume','linux-device'} {$key='~volume'}
        {$_ -in 'drive','usb-drive','special'} {$key='path:' + $Entry.Path}
        'folder' {$key='~folder'}
        default {$ext=[IO.Path]::GetExtension($Entry.Name).ToLowerInvariant(); $key = if ($ext) {$ext} else {'~file'}}
    }
    foreach ($images in @($Pane.Large,$smallIcons)) {
        if ($images.Images.ContainsKey($key)) {continue}
        $size = $images.ImageSize.Width
        $image = switch -Wildcard ($key) {
            '~device' {[UsbProxyUi.ShellIcons]::Stock([UsbProxyUi.ShellIcons]::DriveRemovable,$size)}
            '~volume' {[UsbProxyUi.ShellIcons]::WithBadge([UsbProxyUi.ShellIcons]::Stock([UsbProxyUi.ShellIcons]::DriveRemovable,$size),$penguin)}
            'path:*' {[UsbProxyUi.ShellIcons]::ForPath($key.Substring(5),$size)}
            '~folder' {[UsbProxyUi.ShellIcons]::ForFile('folder',$true,$size)}
            '~file' {[UsbProxyUi.ShellIcons]::ForFile('file',$false,$size)}
            default {[UsbProxyUi.ShellIcons]::ForFile('file' + $key,$false,$size)}
        }
        $images.Images.Add($key,$image)
    }
    return $key
}
function New-Row($Entry,$Pane,[string]$Column1,[string]$Column3) {
    $item = New-Object Windows.Forms.ListViewItem($Entry.Name,(Get-IconKey $Entry $Pane))
    $null = $item.SubItems.Add($Column1)
    $null = $item.SubItems.Add([string]$Entry.TypeName)
    $null = $item.SubItems.Add($Column3)
    $item.Tag = $Entry
    return $item
}
function Set-Items($Pane,$Items,[string[]]$Columns,[int[]]$Widths,[bool]$Sorted) {
    $list = $Pane.List
    $list.BeginUpdate()
    $list.ListViewItemSorter = $null
    $list.Items.Clear()
    $list.Columns.Clear()
    for ($i=0;$i -lt $Columns.Count;$i++) {
        $column = $list.Columns.Add($Columns[$i],[int]($Widths[$i]*$scale))
        if ($i -eq 3) {$column.TextAlign='Right'}
    }
    if (@($Items).Count) {$list.Items.AddRange([Windows.Forms.ListViewItem[]]@($Items))}
    # 本機 keeps its natural order; folders sort like Explorer.
    if ($Sorted) {$list.ListViewItemSorter = $Pane.Sorter}
    $list.EndUpdate()
}
function Set-Empty($Pane,[string]$Text) { $Pane.Empty.Text=$Text; $Pane.Empty.Visible=[bool]$Text }

function New-Entry([string]$Kind,[string]$Name,[string]$Path,[string]$TypeName) {
    $entry = New-Object UsbProxyUi.FileEntry
    $entry.Kind=$Kind; $entry.Name=$Name; $entry.Path=$Path; $entry.TypeName=$TypeName
    return $entry
}

# --- Windows pane ---
function Show-WinComputer {
    $items = @()
    $special = @(
        @('桌面',[Environment]::GetFolderPath('Desktop')),
        @('下載',(Join-Path $env:USERPROFILE 'Downloads')),
        @('文件',[Environment]::GetFolderPath('MyDocuments')),
        @('圖片',[Environment]::GetFolderPath('MyPictures')))
    foreach ($s in $special) {
        if ($s[1] -and (Test-Path -LiteralPath $s[1])) {$items += New-Row (New-Entry 'special' $s[0] $s[1] '資料夾') $win '' ''}
    }
    $letters = @{}
    foreach ($d in $script:usb) { foreach ($l in @($d.drives)) { if ($l) {$letters[[string]$l] = $d} } }
    foreach ($drive in [IO.DriveInfo]::GetDrives()) {
        if (-not $drive.IsReady) {continue}
        $letter = $drive.Name.TrimEnd('\')
        $label = if ($drive.VolumeLabel) {$drive.VolumeLabel} elseif ([string]$drive.DriveType -eq 'Fixed') {'本機磁碟'} else {'磁碟'}
        $usbDevice = $letters[$letter]
        $entry = New-Entry $(if ($usbDevice) {'usb-drive'} else {'drive'}) ('{0} ({1})' -f $label,$letter) $drive.Name ''
        $entry.TypeName = if ($usbDevice) {'USB 隨身碟 · 可切換到 Linux'} else {[UsbProxyUi.ShellIcons]::PathTypeName($drive.Name)}
        $entry.Size = $drive.TotalSize
        if ($usbDevice) {$entry.Uuid = [string]$usbDevice.uuid; $entry.Device = $entry.Name}
        $items += New-Row $entry $win ((Format-Size $drive.AvailableFreeSpace) + ' 可用') (Format-Size $drive.TotalSize)
    }
    # Drives Windows cannot mount (no letter) can still be switched to Linux.
    foreach ($d in $script:usb) {
        if (@($d.drives | Where-Object {$_}).Count) {continue}
        $name = (($d.Manufacturer,$d.Product) | Where-Object {$_ -and $_.Trim()} | ForEach-Object {$_.Trim()}) -join ' '
        $entry = New-Entry 'device' $(if ($name) {$name} else {'USB 隨身碟'}) '' 'USB 隨身碟 · Windows 無法存取 · 可切換到 Linux'
        $entry.Uuid = [string]$d.uuid; $entry.Device = $entry.Name
        $items += New-Row $entry $win '' ''
    }
    Set-Items $win $items @('名稱','可用空間','類型','總大小') @(190,110,170,80) $false
    Set-Empty $win ''
    $win.Address.Text = '本機'
}
function Show-WinFolder([string]$Path) {
    $directory = New-Object IO.DirectoryInfo($Path)
    $hidden = [IO.FileAttributes]::Hidden -bor [IO.FileAttributes]::System
    $children = @($directory.EnumerateFileSystemInfos() | Where-Object { -not ($_.Attributes -band $hidden) })
    $items = foreach ($child in $children) {
        $folder = [bool]($child.Attributes -band [IO.FileAttributes]::Directory)
        $entry = New-Entry $(if ($folder) {'folder'} else {'file'}) $child.Name $child.FullName (Get-TypeName $child.Name $folder)
        $entry.Modified = $child.LastWriteTime
        if (-not $folder) {$entry.Size = $child.Length}
        New-Row $entry $win ($entry.Modified.ToString('yyyy/MM/dd HH:mm')) $(if ($folder) {''} else {Format-Size $entry.Size})
    }
    Set-Items $win @($items) @('名稱','修改日期','類型','大小') @(210,130,130,80) $true
    Set-Empty $win $(if (@($items).Count) {''} else {"這個資料夾是空的`n`n把右側 Linux 的檔案拖到這裡即可複製到電腦"})
    $win.Address.Text = '本機  ›  ' + (($Path.TrimEnd('\') -split '\\' | Where-Object {$_}) -join '  ›  ')
}

# --- Linux pane ---
function Show-LinComputer {
    $items = @()
    foreach ($v in $script:volumes) {
        $label = if ($v.label) {$v.label} else {'USB 隨身碟'}
        $drive = Get-DriveOf $v
        $entry = New-Entry 'volume' ('{0} ({1})' -f $label,([string]$v.fstype).ToUpper()) '' ''
        $entry.Device = [string]$v.path; $entry.Size = [long]$v.size
        $entry.TypeName = 'USB 隨身碟' + $(if ($drive -and $drive.name) {' · ' + $drive.name} else {''})
        if ($drive) {$entry.Uuid = [string]$drive.uuid}
        $free = if ($script:volumeInfo.ContainsKey($entry.Device)) {(Format-Size $script:volumeInfo[$entry.Device].free) + ' 可用'} else {''}
        $items += New-Row $entry $lin $free (Format-Size $v.size)
    }
    foreach ($drive in $script:attached) {
        if (@($script:volumes | Where-Object { $d = Get-DriveOf $_; $d -and $d.uuid -eq $drive.uuid }).Count) {continue}
        $entry = New-Entry 'linux-device' $(if ($drive.name) {$drive.name} else {'USB 隨身碟'}) '' '找不到支援的分割區'
        $entry.Uuid = [string]$drive.uuid
        $items += New-Row $entry $lin '' ''
    }
    Set-Items $lin $items @('名稱','可用空間','類型','總大小') @(190,110,170,80) $false
    $message = if ($items.Count) {''}
        elseif ($script:vmState -eq 'notinstalled') {"尚未安裝 Linux 環境`n`n按上方「安裝」開始（約 3～5 分鐘，不需要網路）"}
        elseif ($script:vmState -ne 'running') {"Linux 未啟動`n`n在左側按兩下隨身碟，即可切換到 Linux（會自動啟動）"}
        else {"沒有隨身碟在 Linux 中`n`n在左側按兩下隨身碟，即可切換到這裡"}
    Set-Empty $lin $message
    $lin.Address.Text = 'Linux'
}
function Show-LinFolder($Data,[string]$Device) {
    $items = foreach ($item in @($Data.entries | Where-Object {$_})) {
        $kind = if ($item.link) {'link'} elseif ($item.dir) {'folder'} else {'file'}
        $entry = New-Entry $kind ([string]$item.name) (Join-RemotePath ([string]$Data.path) $item.name) ''
        $entry.Device = $Device; $entry.Size = [long]$item.size
        $entry.Modified = [DateTimeOffset]::FromUnixTimeSeconds([long]$item.mtime).LocalDateTime
        $entry.TypeName = if ($entry.IsLink) {'符號連結（不支援）'} else {Get-TypeName $entry.Name $entry.IsFolder}
        New-Row $entry $lin ($entry.Modified.ToString('yyyy/MM/dd HH:mm')) $(if ($kind -eq 'file') {Format-Size $entry.Size} else {''})
    }
    Set-Items $lin @($items) @('名稱','修改日期','類型','大小') @(210,130,130,80) $true
    Set-Empty $lin $(if (@($items).Count) {''} else {"這個資料夾是空的`n`n把左側或檔案總管的檔案拖到這裡即可複製到隨身碟"})
    $script:volumeInfo[$Device] = @{free=[long]$Data.free; total=[long]$Data.total}
    $lin.Address.Text = (@('Linux',(Get-VolumeLabel $Device)) + @(([string]$Data.path) -split '/' | Where-Object {$_})) -join '  ›  '
    if ($script:editAfter) {
        foreach ($item in $lin.List.Items) {
            if ($item.Text -eq $script:editAfter) {$lin.List.SelectedItems.Clear(); $item.Selected=$true; $item.Focused=$true; $item.EnsureVisible(); $item.BeginEdit()}
        }
        $script:editAfter = $null
    }
}

# --- Navigation ---
function Test-SameLocation($A,$B) { $A.kind -eq $B.kind -and [string]$A.path -eq [string]$B.path -and [string]$A.device -eq [string]$B.device }
function Navigate($Pane,[hashtable]$Target,[switch]$NoHistory) {
    if ($Pane.Key -eq 'lin' -and $Target.kind -eq 'folder') {
        if ($script:busy) {return}
        # Linux folders are read by the worker; the view switches when the listing arrives.
        $script:pendingNav = @{target=$Target; push=(-not $NoHistory)}
        Begin-Operation @{action='list'; device=$Target.device; path=$Target.path}
        return
    }
    try {
        if ($Target.kind -eq 'pc') {if ($Pane.Key -eq 'win') {Show-WinComputer} else {Show-LinComputer}}
        else {Show-WinFolder $Target.path}
    } catch {
        [Windows.Forms.MessageBox]::Show(('無法開啟：' + $_.Exception.Message),'USB Proxy') | Out-Null
        return
    }
    if (-not $NoHistory -and -not (Test-SameLocation $Pane.Location $Target)) {$Pane.History.Add($Pane.Location)}
    $Pane.Location = $Target
    Update-Counts; Update-State
}
function Go-Back($Pane) {
    if (-not $Pane -or $Pane.History.Count -eq 0) {return}
    if ($Pane.Key -eq 'lin' -and $script:busy) {return}
    $target = $Pane.History[$Pane.History.Count-1]
    $Pane.History.RemoveAt($Pane.History.Count-1)
    Navigate $Pane $target -NoHistory
}
function Go-Up($Pane) {
    if (-not $Pane) {return}
    $location = $Pane.Location
    if ($location.kind -ne 'folder') {return}
    if ($Pane.Key -eq 'win') {
        $parent = (New-Object IO.DirectoryInfo($location.path.TrimEnd('\') + '\')).Parent
        if (-not $parent) {Navigate $win @{kind='pc'}} else {Navigate $win @{kind='folder'; path=$parent.FullName}}
    } elseif ($location.path) {Navigate $lin @{kind='folder'; device=$location.device; path=(Get-ParentPath $location.path)}}
    else {Navigate $lin @{kind='pc'}}
}
function Refresh-Pane($Pane) {
    if ($Pane.Location.kind -eq 'pc') {Begin-Operation @{action='status'}}
    else {Navigate $Pane $Pane.Location -NoHistory}
}
function Reset-Lin {
    $lin.History.Clear()
    $lin.Location = @{kind='pc'}
    Show-LinComputer
}

function Update-Counts {
    $pane = $script:active
    $parts = @(('{0}：{1} 個項目' -f $(if ($pane.Key -eq 'win') {'Windows'} else {'Linux'}),$pane.List.Items.Count))
    if ($pane.List.SelectedItems.Count) {$parts += ('已選取 {0} 個' -f $pane.List.SelectedItems.Count)}
    if ($pane.Key -eq 'lin' -and $pane.Location.kind -eq 'folder' -and $script:volumeInfo.ContainsKey($pane.Location.device)) {
        $v = $script:volumeInfo[$pane.Location.device]
        $parts += ('可用 {0}，共 {1}' -f (Format-Size $v.free),(Format-Size $v.total))
    }
    if ($script:linuxClip) {$parts += ('剪貼簿：{0} 個 Linux 項目' -f @($script:linuxClip.paths).Count)}
    Set-Message ($parts -join '  ·  ')
}

function Update-State {
    $idle = -not $script:busy
    $running = $script:vmState -eq 'running'
    $starting = $script:busy -and $script:pendingAction -in 'start','setup'
    switch ($script:vmState) {
        'running' {
            if ($script:ready) {$linuxTitle.Text='Linux 已就緒'; $dot.BadgeColor=[UsbProxyUi.Theme]::Green}
            else {$linuxTitle.Text='Linux 啟動中'; $dot.BadgeColor=[UsbProxyUi.Theme]::Orange}
        }
        'stopping' {$linuxTitle.Text='Linux 正在關機'; $dot.BadgeColor=[UsbProxyUi.Theme]::Orange}
        'poweroff' {$linuxTitle.Text='Linux 未啟動'; $dot.BadgeColor=[UsbProxyUi.Theme]::Gray}
        'unknown' {$linuxTitle.Text='正在連線 Linux…'; $dot.BadgeColor=[UsbProxyUi.Theme]::Gray}
        'notinstalled' {$linuxTitle.Text='尚未安裝 Linux 環境'; $dot.BadgeColor=[UsbProxyUi.Theme]::Orange}
        default {$linuxTitle.Text='Linux 狀態：' + $script:vmState; $dot.BadgeColor=[UsbProxyUi.Theme]::Gray}
    }
    if ($starting) {$linuxTitle.Text = if ($script:pendingAction -eq 'setup') {'正在安裝 Linux 環境'} else {'Linux 啟動中'}; $dot.BadgeColor=[UsbProxyUi.Theme]::Orange}
    $dot.Dimmed = -not $running -and -not $starting
    if (-not $starting) {
        $count = $script:attached.Count
        $linuxDetail.Text = if ($count -gt 1) {('{0} 支隨身碟在 Linux 中，可在右側互相傳輸；Windows 暫時無法使用它們' -f $count)}
            elseif ($count -eq 1) {('「{0}」在 Linux 中，Windows 暫時無法使用' -f $(if ($script:attached[0].name) {$script:attached[0].name} else {'隨身碟'}))}
            elseif ($running) {'在左側按兩下隨身碟，即可切換到 Linux 管理'}
            elseif ($script:vmState -eq 'poweroff') {'在左側按兩下隨身碟時會自動啟動'}
            elseif ($script:vmState -eq 'notinstalled') {'首次使用：按「安裝」設定 VirtualBox 與 Linux（約 3～5 分鐘，可離線）'}
            else {$linuxDetail.Text}
    }
    if ($script:vmState -eq 'notinstalled') {$linuxToggle.Text='安裝'; $linuxToggle.Kind='Primary'}
    elseif ($running) {$linuxToggle.Text='關閉 Linux'; $linuxToggle.Kind='Secondary'}
    else {$linuxToggle.Text='啟動 Linux'; $linuxToggle.Kind='Primary'}
    $linuxToggle.Enabled = $idle -and $script:vmState -ne 'stopping'
    foreach ($pane in $panes) {
        $free = $idle -or $pane.Key -eq 'win'
        $pane.Back.Enabled = $pane.History.Count -gt 0 -and $free
        $pane.Up.Enabled = $pane.Location.kind -eq 'folder' -and $free
    }
}

# --- Actions ---
# First run: install VirtualBox (one administrator prompt), build Linux, then start it.
function Start-Setup {
    if ($script:busy) {return}
    $text = '第一次使用需要安裝 VirtualBox 並建立 Linux 環境，約需 3～5 分鐘，不需要網路。' + "`n`n" + '安裝 VirtualBox 時 Windows 會詢問系統管理員權限，請按「是」。要現在安裝嗎？'
    $choice = [Windows.Forms.MessageBox]::Show($text,'安裝 USB Proxy',[Windows.Forms.MessageBoxButtons]::OKCancel,[Windows.Forms.MessageBoxIcon]::Information)
    if ($choice -eq [Windows.Forms.DialogResult]::OK) {Begin-Operation @{action='setup'}}
}
function Switch-ToLinux($Entry) {
    if ($script:busy -or -not $Entry) {return}
    if ($script:vmState -eq 'notinstalled') {Start-Setup; return}
    $text = ('要將「{0}」切換到 Linux 嗎？' -f $Entry.Name) + "`n`n" + '切換後 Windows 會暫時無法使用這個隨身碟，請先關閉其中開啟的檔案。在右側按右鍵「退出」即可交還 Windows。'
    if ($script:vmState -ne 'running') {$text += "`n`n" + 'Linux 尚未啟動，會先自動啟動。'}
    if ($Entry.Kind -eq 'usb-drive') {
        $choice = [Windows.Forms.MessageBox]::Show(($text + "`n`n" + '是：切換到 Linux　否：在 Windows 中開啟'),'切換到 Linux',[Windows.Forms.MessageBoxButtons]::YesNoCancel,[Windows.Forms.MessageBoxIcon]::Question)
        if ($choice -eq [Windows.Forms.DialogResult]::No) {Navigate $win @{kind='folder'; path=$Entry.Path}; return}
        if ($choice -ne [Windows.Forms.DialogResult]::Yes) {return}
    } else {
        $choice = [Windows.Forms.MessageBox]::Show($text,'切換到 Linux',[Windows.Forms.MessageBoxButtons]::OKCancel,[Windows.Forms.MessageBoxIcon]::Question)
        if ($choice -ne [Windows.Forms.DialogResult]::OK) {return}
    }
    # Leave the drive's folders in the Windows pane before Windows loses the drive.
    if ($win.Location.kind -eq 'folder' -and $Entry.Path -and $win.Location.path.StartsWith($Entry.Path,[StringComparison]::OrdinalIgnoreCase)) {Navigate $win @{kind='pc'}}
    $script:switchTarget = @{uuid=$Entry.Uuid; name=$Entry.Device}
    if ($script:vmState -eq 'running' -and $script:ready) {Begin-Operation @{action='attach'; uuid=$Entry.Uuid; name=$Entry.Device}}
    else {Begin-Operation @{action='start'}}
}
function Eject-Drive([string]$Uuid) {
    if ($script:busy -or -not $Uuid) {return}
    Begin-Operation @{action='eject'; uuid=$Uuid}
}
function Open-Entry($Pane) {
    $selected = @(Get-Selected $Pane)
    if ($selected.Count -ne 1) {return}
    $entry = $selected[0]
    switch ($entry.Kind) {
        {$_ -in 'device','usb-drive'} {Switch-ToLinux $entry}
        {$_ -in 'drive','special'} {Navigate $win @{kind='folder'; path=$entry.Path}}
        'volume' {Navigate $lin @{kind='folder'; device=$entry.Device; path=''}}
        'linux-device' {[Windows.Forms.MessageBox]::Show('這個隨身碟沒有支援的分割區（FAT32、exFAT、NTFS、ext4）。','USB Proxy') | Out-Null}
        'link' {[Windows.Forms.MessageBox]::Show('不支援開啟符號連結。','USB Proxy') | Out-Null}
        'folder' {
            if ($Pane.Key -eq 'win') {Navigate $win @{kind='folder'; path=$entry.Path}}
            else {Navigate $lin @{kind='folder'; device=$entry.Device; path=$entry.Path}}
        }
        default {
            if ($Pane.Key -eq 'win') {try {Start-Process -FilePath $entry.Path} catch {[Windows.Forms.MessageBox]::Show('沒有可開啟這個檔案的程式。','USB Proxy') | Out-Null}}
            elseif (-not $script:busy) {Begin-Operation @{action='fetch'; device=$entry.Device; path=$entry.Path; size=$entry.Size; name=$entry.Name}}
        }
    }
}
function Save-LinuxFile {
    $selected = @(Get-Selected $lin)
    if ($script:busy -or $selected.Count -ne 1 -or $selected[0].Kind -ne 'file') {return}
    $dialog = New-Object Windows.Forms.SaveFileDialog
    $dialog.FileName = $selected[0].Name; $dialog.OverwritePrompt = $true
    if ($dialog.ShowDialog() -eq 'OK') {
        Begin-Operation @{action='fetch'; device=$selected[0].Device; path=$selected[0].Path; size=$selected[0].Size; name=$selected[0].Name; saveTo=$dialog.FileName}
    }
    $dialog.Dispose()
}
function Remove-LinuxItems {
    $selected = @(Get-Selected $lin | Where-Object {$_.Kind -in 'file','folder','link'})
    if ($script:busy -or $selected.Count -eq 0) {return}
    $what = if ($selected.Count -eq 1) {'「' + $selected[0].Name + '」'} else {('這 {0} 個項目' -f $selected.Count)}
    $choice = [Windows.Forms.MessageBox]::Show(('確定要刪除{0}嗎？' -f $what),'刪除',[Windows.Forms.MessageBoxButtons]::OKCancel,[Windows.Forms.MessageBoxIcon]::Warning,[Windows.Forms.MessageBoxDefaultButton]::Button2)
    if ($choice -ne [Windows.Forms.DialogResult]::OK) {return}
    $script:followUp = @{nav=$lin.Location}
    Begin-Operation @{action='delete'; device=$lin.Location.device; paths=@($selected | ForEach-Object {$_.Path})}
}
function Rename-LinuxItem {
    $items = @($lin.List.SelectedItems)
    if ($script:busy -or $items.Count -ne 1 -or $lin.Location.kind -ne 'folder') {return}
    $items[0].BeginEdit()
}
function New-LinuxFolder {
    if ($script:busy -or $lin.Location.kind -ne 'folder') {return}
    $names = @($lin.List.Items | ForEach-Object {$_.Text})
    $name = '新增資料夾'; $n = 2
    while ($names -contains $name) {$name = '新增資料夾 ({0})' -f $n; $n++}
    $script:editAfter = $name
    $script:followUp = @{nav=$lin.Location}
    Begin-Operation @{action='mkdir'; device=$lin.Location.device; path=$lin.Location.path; name=$name}
}

# Copies in every direction: Windows -> Linux (copy), Linux -> Linux (transfer), Linux -> Windows (download).
function Copy-ToLinux([string[]]$Paths,[string]$Device,[string]$Folder) {
    $Paths = @($Paths | Where-Object {$_})
    if ($script:busy -or -not $Device -or $Paths.Count -eq 0) {return}
    if ($lin.Location.kind -eq 'folder') {$script:followUp = @{nav=$lin.Location}}
    Begin-Operation @{action='copy'; device=$Device; dest=$Folder; paths=$Paths}
}
function Transfer-InLinux($Clip,[string]$Device,[string]$Folder) {
    if ($script:busy -or -not $Clip -or -not $Device) {return}
    # Dropping items back into their own folder is a no-op.
    if ($Clip.device -eq $Device -and @($Clip.paths | Where-Object { (Get-ParentPath $_) -ne $Folder }).Count -eq 0) {return}
    if ($lin.Location.kind -eq 'folder') {$script:followUp = @{nav=$lin.Location}}
    Begin-Operation @{action='transfer'; source=$Clip.device; paths=@($Clip.paths); device=$Device; dest=$Folder}
}
function Download-ToWindows($Clip,[string]$Folder) {
    if ($script:busy -or -not $Clip -or -not $Folder) {return}
    $script:followUp = @{refreshWin=$true}
    Begin-Operation @{action='download'; device=$Clip.device; paths=@($Clip.paths); destination=$Folder}
}
function Get-LinuxSelection {
    $selected = @(Get-Selected $lin | Where-Object {$_.Kind -in 'file','folder'})
    if ($selected.Count -eq 0) {return $null}
    return @{device=$selected[0].Device; paths=@($selected | ForEach-Object {$_.Path})}
}
function Copy-Selection($Pane) {
    if ($Pane.Key -eq 'lin') {
        $script:linuxClip = Get-LinuxSelection
    } else {
        $paths = @(Get-Selected $win | Where-Object {$_.Kind -in 'file','folder'} | ForEach-Object {$_.Path})
        if ($paths.Count) {
            $collection = New-Object Collections.Specialized.StringCollection
            foreach ($p in $paths) {$null = $collection.Add($p)}
            [Windows.Forms.Clipboard]::SetFileDropList($collection)
            $script:linuxClip = $null
        }
    }
    Update-Counts
}
function Paste-Into($Pane) {
    if ($script:busy) {return}
    if ($Pane.Key -eq 'lin') {
        if ($lin.Location.kind -ne 'folder') {return}
        if ($script:linuxClip) {Transfer-InLinux $script:linuxClip $lin.Location.device $lin.Location.path}
        elseif ([Windows.Forms.Clipboard]::ContainsFileDropList()) {Copy-ToLinux @([Windows.Forms.Clipboard]::GetFileDropList()) $lin.Location.device $lin.Location.path}
    } elseif ($win.Location.kind -eq 'folder' -and $script:linuxClip) {
        Download-ToWindows $script:linuxClip $win.Location.path
    }
}
# Sends the selection to the folder open in the other pane.
function Send-ToOtherPane($Pane) {
    if ($Pane.Key -eq 'win') {
        if ($lin.Location.kind -ne 'folder') {[Windows.Forms.MessageBox]::Show('請先在右側開啟隨身碟上的資料夾。','USB Proxy') | Out-Null; return}
        Copy-ToLinux @(Get-Selected $win | Where-Object {$_.Kind -in 'file','folder'} | ForEach-Object {$_.Path}) $lin.Location.device $lin.Location.path
    } else {
        if ($win.Location.kind -ne 'folder') {[Windows.Forms.MessageBox]::Show('請先在左側開啟電腦上的資料夾。','USB Proxy') | Out-Null; return}
        Download-ToWindows (Get-LinuxSelection) $win.Location.path
    }
}

# Files opened from the USB are edited in a local copy; saved changes are offered back to the volume.
function Watch-OpenFiles {
    if ($script:busy -or $script:prompting -or $script:openFiles.Count -eq 0) {return}
    if (((Get-Date) - $script:lastOpenCheck).TotalSeconds -lt 1.5) {return}
    $script:lastOpenCheck = Get-Date
    foreach ($file in @($script:openFiles)) {
        if (-not (Test-Path -LiteralPath $file.Local)) {continue}
        $stamp = (Get-Item -LiteralPath $file.Local).LastWriteTimeUtc
        if ($stamp -eq $file.Stamp) {$file.Seen=$null; continue}
        # Wait until the timestamp is stable so a save in progress is not uploaded half-written.
        if ($file.Seen -ne $stamp) {$file.Seen=$stamp; continue}
        $file.Stamp=$stamp; $file.Seen=$null
        if (-not @($script:volumes | Where-Object {$_.path -eq $file.Device}).Count) {continue}
        $script:prompting = $true
        try {
            $choice = [Windows.Forms.MessageBox]::Show(('「{0}」已在電腦上修改。要將變更寫回隨身碟嗎？隨身碟上的原檔案會被取代。' -f $file.Name),'寫回隨身碟',[Windows.Forms.MessageBoxButtons]::YesNo,[Windows.Forms.MessageBoxIcon]::Question)
        } finally {$script:prompting = $false}
        if ($choice -eq [Windows.Forms.DialogResult]::Yes) {
            if ($lin.Location.kind -eq 'folder') {$script:followUp = @{nav=$lin.Location}}
            Begin-Operation @{action='copy'; device=$file.Device; dest=(Get-ParentPath $file.Remote); paths=@($file.Local); overwrite=$true; targetName=$file.Name}
        }
        return
    }
}

function Set-Busy([bool]$Value) {
    $script:busy = $Value
    if (-not $Value) {$bootProgress.Visible=$false; $transfer.Visible=$false; $transferGap.Visible=$false; $lin.List.Cursor=[Windows.Forms.Cursors]::Default}
    elseif ($script:pendingAction -in 'list','status','delete','rename','mkdir','eject','attach') {$lin.List.Cursor=[Windows.Forms.Cursors]::AppStarting}
    Update-State
}
function Update-Guest($Guest) {
    $script:volumes = @()
    if ($Guest) {
        $script:ready = [bool]$Guest.ready
        $script:volumes = @($Guest.volumes | Where-Object {$_})
    }
}

# --- Drag and drop between the panes and from Explorer ---
$linuxFormat = 'UsbProxy.LinuxItems'
function Get-DropTarget($Pane,$E) {
    $point = $Pane.List.PointToClient((New-Object Drawing.Point($E.X,$E.Y)))
    $item = $Pane.List.GetItemAt($point.X,$point.Y)
    $entry = if ($item) {$item.Tag} else {$null}
    if ($Pane.Key -eq 'win') {
        if ($entry -and $entry.Kind -in 'folder','drive','special') {return @{path=$entry.Path}}
        if ($win.Location.kind -eq 'folder') {return @{path=$win.Location.path}}
        return $null
    }
    if ($entry -and $entry.Kind -eq 'folder') {return @{device=$entry.Device; path=$entry.Path}}
    if ($entry -and $entry.Kind -eq 'volume') {return @{device=$entry.Device; path=''}}
    if ($lin.Location.kind -eq 'folder') {return @{device=$lin.Location.device; path=$lin.Location.path}}
    return $null
}
$onDragOver = {
    param($s,$e)
    $pane = Get-PaneOf $s
    $effect = [Windows.Forms.DragDropEffects]::None
    if ($pane -and -not $script:busy -and (Get-DropTarget $pane $e)) {
        $fromLinux = $e.Data.GetDataPresent($linuxFormat)
        if ($pane.Key -eq 'lin' -and ($fromLinux -or $e.Data.GetDataPresent([Windows.Forms.DataFormats]::FileDrop))) {$effect = [Windows.Forms.DragDropEffects]::Copy}
        if ($pane.Key -eq 'win' -and $fromLinux) {$effect = [Windows.Forms.DragDropEffects]::Copy}
    }
    $e.Effect = $effect
}
$onDragDrop = {
    param($s,$e)
    $pane = Get-PaneOf $s
    $target = if ($pane) {Get-DropTarget $pane $e} else {$null}
    if (-not $target) {return}
    if ($e.Data.GetDataPresent($linuxFormat)) {
        $clip = [string]$e.Data.GetData($linuxFormat) | ConvertFrom-Json
        if ($pane.Key -eq 'lin') {Transfer-InLinux $clip $target.device $target.path}
        else {Download-ToWindows $clip $target.path}
    } elseif ($pane.Key -eq 'lin') {
        Copy-ToLinux ([string[]]$e.Data.GetData([Windows.Forms.DataFormats]::FileDrop)) $target.device $target.path
    }
}
foreach ($pane in $panes) {
    foreach ($target in @($pane.List,$pane.Empty)) {
        $target.AllowDrop = $true
        $target.Add_DragEnter($onDragOver); $target.Add_DragOver($onDragOver); $target.Add_DragDrop($onDragDrop)
    }
}
$win.List.Add_ItemDrag({
    $paths = @(Get-Selected $win | Where-Object {$_.Kind -in 'file','folder'} | ForEach-Object {$_.Path})
    if (-not $paths.Count) {return}
    $data = New-Object Windows.Forms.DataObject
    $collection = New-Object Collections.Specialized.StringCollection
    foreach ($path in $paths) {$null = $collection.Add($path)}
    $data.SetFileDropList($collection)
    $null = $win.List.DoDragDrop($data,[Windows.Forms.DragDropEffects]::Copy)
})
$lin.List.Add_ItemDrag({
    $clip = Get-LinuxSelection
    if (-not $clip) {return}
    $data = New-Object Windows.Forms.DataObject
    $data.SetData($linuxFormat,($clip | ConvertTo-Json -Compress))
    $null = $lin.List.DoDragDrop($data,[Windows.Forms.DragDropEffects]::Copy)
})

# --- Pane events: activation, keys, zoom, rename ---
$zoomSizes = @(0,0,48,64,96,128,192,256)
function Set-Zoom($Pane,[int]$Step) {
    $Step = [Math]::Max(0,[Math]::Min($zoomSizes.Count-1,$Step))
    $ui['zoom_' + $Pane.Key] = $Step
    $list = $Pane.List
    if ($Step -ge 2) {
        $ui['icon_' + $Pane.Key] = $Step
        # Image lists hold at most 256 px images.
        $px = [Math]::Min(256,[int]($zoomSizes[$Step]*$scale))
        if ($Pane.Large.ImageSize.Width -ne $px) {
            $list.BeginUpdate()
            $Pane.Large.Images.Clear()
            $Pane.Large.ImageSize = New-Object Drawing.Size($px,$px)
            foreach ($item in $list.Items) {$null = Get-IconKey $item.Tag $Pane}
            $list.LargeImageList = $null; $list.LargeImageList = $Pane.Large
            $list.EndUpdate()
        }
        $list.SetIconSpacing([int]($px + 44*$scale),[int]($px + 48*$scale))
        $list.View = 'LargeIcon'
    } elseif ($Step -eq 1) {$list.View = 'List'}
    else {$list.View = 'Details'}
    $Pane.Settling = $true
    $Pane.View.SelectedIndex = if ($Step -ge 2) {0} elseif ($Step -eq 1) {1} else {2}
    $Pane.Settling = $false
}
foreach ($pane in $panes) {
    $pane.View.Add_SelectedIndexChanged({
        param($s,$e)
        $p = Get-PaneOf $s
        if (-not $p -or $p.Settling) {return}
        switch ($p.View.SelectedIndex) { 0 {Set-Zoom $p ([int]$ui['icon_' + $p.Key])} 1 {Set-Zoom $p 1} default {Set-Zoom $p 0} }
    })
    $pane.List.Add_CtrlWheel({ param($s,$e) $p = Get-PaneOf $s; Set-Zoom $p ([int]$ui['zoom_' + $p.Key] + [Math]::Sign($e.Delta)) })
    $pane.List.Add_HandleCreated({ param($s,$e) $p = Get-PaneOf $s; if ($p) {Set-Zoom $p ([int]$ui['zoom_' + $p.Key])} })
    $pane.List.Add_Enter({ param($s,$e) $script:active = Get-PaneOf $s; Update-Counts })
    $pane.List.Add_ItemActivate({ param($s,$e) Open-Entry (Get-PaneOf $s) })
    $pane.List.Add_SelectedIndexChanged({ param($s,$e) $script:active = Get-PaneOf $s; Update-Counts })
    $pane.List.Add_KeyDown({
        param($s,$e)
        $p = Get-PaneOf $s
        if ($e.Alt -and $e.KeyCode -eq 'Left') {Go-Back $p; $e.Handled=$true; return}
        if ($e.Alt -and $e.KeyCode -eq 'Up') {Go-Up $p; $e.Handled=$true; return}
        if ($e.Control -and $e.KeyCode -eq 'A') {foreach ($item in $p.List.Items) {$item.Selected=$true}; $e.Handled=$true; return}
        if ($e.Control -and $e.KeyCode -eq 'C') {Copy-Selection $p; $e.Handled=$true; return}
        if ($e.Control -and $e.KeyCode -eq 'V') {Paste-Into $p; $e.Handled=$true; return}
        switch ($e.KeyCode) {
            'Back' {Go-Back $p}
            'F5' {Refresh-Pane $p}
            'Delete' {if ($p.Key -eq 'lin') {Remove-LinuxItems}}
            'F2' {if ($p.Key -eq 'lin') {Rename-LinuxItem}}
        }
    })
    Set-Zoom $pane ([int]$ui['zoom_' + $pane.Key])
}
$lin.List.LabelEdit = $true
$lin.List.Add_BeforeLabelEdit({ param($s,$e) if ($script:busy -or $lin.Location.kind -ne 'folder' -or $lin.List.Items[$e.Item].Tag.IsLink) {$e.CancelEdit=$true} })
$lin.List.Add_AfterLabelEdit({
    param($s,$e)
    $entry = $lin.List.Items[$e.Item].Tag
    $name = if ($null -ne $e.Label) {$e.Label.Trim()} else {''}
    # The list keeps the old name until the rename succeeds and the folder is re-read.
    $e.CancelEdit = $true
    if (-not $name -or $name -eq $entry.Name -or $script:busy) {return}
    $script:followUp = @{nav=$lin.Location}
    Begin-Operation @{action='rename'; device=$entry.Device; path=$entry.Path; name=$name}
})

# --- Context menus ---
function New-Menu {
    $menu = New-Object Windows.Forms.ContextMenuStrip
    $menu.Renderer = New-Object UsbProxyUi.MenuRenderer
    $menu.Font = $bodyFont
    return $menu
}
function Add-MenuItem($Menu,[string]$Text,[scriptblock]$Handler,[string]$Keys='') {
    $item = New-Object Windows.Forms.ToolStripMenuItem($Text)
    if ($Keys) {$item.ShortcutKeyDisplayString = $Keys}
    $item.Add_Click($Handler)
    $null = $Menu.Items.Add($item)
    return $item
}
$winMenu = New-Menu
$wOpen = Add-MenuItem $winMenu '開啟' { Open-Entry $win } 'Enter'
$wSwitch = Add-MenuItem $winMenu '切換到 Linux' { Switch-ToLinux @(Get-Selected $win)[0] }
$wSend = Add-MenuItem $winMenu '複製到右側 Linux 資料夾' { Send-ToOtherPane $win }
$wCopy = Add-MenuItem $winMenu '複製' { Copy-Selection $win } 'Ctrl+C'
$wPaste = Add-MenuItem $winMenu '貼上（從 Linux）' { Paste-Into $win } 'Ctrl+V'
$wShow = Add-MenuItem $winMenu '在檔案總管中顯示' { $e=@(Get-Selected $win)[0]; if ($e.Path) {Start-Process explorer.exe ('/select,"' + $e.Path + '"')} }
$null = $winMenu.Items.Add((New-Object Windows.Forms.ToolStripSeparator))
$wRefresh = Add-MenuItem $winMenu '重新整理' { Refresh-Pane $win } 'F5'
$winMenu.Add_Opening({
    $selected = @(Get-Selected $win); $kind = if ($selected.Count -eq 1) {$selected[0].Kind} else {''}
    $files = @($selected | Where-Object {$_.Kind -in 'file','folder'}).Count
    $wOpen.Visible = $selected.Count -eq 1 -and $kind -ne 'device'
    $wSwitch.Visible = $kind -in 'device','usb-drive'; $wSwitch.Enabled = -not $script:busy
    $wSend.Visible = $files -gt 0; $wSend.Enabled = -not $script:busy
    $wCopy.Visible = $files -gt 0
    $wPaste.Visible = $win.Location.kind -eq 'folder'; $wPaste.Enabled = -not $script:busy -and [bool]$script:linuxClip
    $wShow.Visible = $selected.Count -eq 1 -and $kind -in 'file','folder'
})
$win.List.ContextMenuStrip = $winMenu; $win.Empty.ContextMenuStrip = $winMenu

function Get-LocationDrive {
    if ($lin.Location.kind -ne 'folder') {return ''}
    $v = @($script:volumes | Where-Object {$_.path -eq $lin.Location.device})
    if ($v.Count) {$d = Get-DriveOf $v[0]; if ($d) {return [string]$d.uuid}}
    return ''
}
$linMenu = New-Menu
$lOpen = Add-MenuItem $linMenu '開啟' { Open-Entry $lin } 'Enter'
$lSave = Add-MenuItem $linMenu '存到電腦…' { Save-LinuxFile }
$lSend = Add-MenuItem $linMenu '複製到左側 Windows 資料夾' { Send-ToOtherPane $lin }
$lCopy = Add-MenuItem $linMenu '複製' { Copy-Selection $lin } 'Ctrl+C'
$lPaste = Add-MenuItem $linMenu '貼上' { Paste-Into $lin } 'Ctrl+V'
$lRename = Add-MenuItem $linMenu '重新命名' { Rename-LinuxItem } 'F2'
$lDelete = Add-MenuItem $linMenu '刪除' { Remove-LinuxItems } 'Del'
$lEject = Add-MenuItem $linMenu '退出（交還 Windows）' { $e=@(Get-Selected $lin)[0]; $u = if ($e -and $e.Uuid) {$e.Uuid} else {Get-LocationDrive}; Eject-Drive $u }
$null = $linMenu.Items.Add((New-Object Windows.Forms.ToolStripSeparator))
$lNewFolder = Add-MenuItem $linMenu '新增資料夾' { New-LinuxFolder }
$lRefresh = Add-MenuItem $linMenu '重新整理' { Refresh-Pane $lin } 'F5'
$linMenu.Add_Opening({
    $selected = @(Get-Selected $lin); $idle = -not $script:busy
    $one = $selected.Count -eq 1; $kind = if ($one) {$selected[0].Kind} else {''}
    $inFolder = $lin.Location.kind -eq 'folder'
    $files = @($selected | Where-Object {$_.Kind -in 'file','folder'}).Count
    $lOpen.Visible = $one
    $lSave.Visible = $inFolder -and $kind -eq 'file'
    $lSend.Visible = $files -gt 0; $lCopy.Visible = $files -gt 0
    $lPaste.Visible = $inFolder; $lPaste.Enabled = $idle -and ([bool]$script:linuxClip -or [Windows.Forms.Clipboard]::ContainsFileDropList())
    $lRename.Visible = $inFolder -and $one; $lDelete.Visible = $inFolder -and $selected.Count -gt 0
    $lEject.Visible = ($kind -in 'volume','linux-device') -or ($inFolder -and $selected.Count -eq 0)
    $lNewFolder.Visible = $inFolder
    foreach ($item in @($lOpen,$lSave,$lSend,$lRename,$lDelete,$lEject,$lNewFolder,$lRefresh)) {$item.Enabled = $idle}
})
$lin.List.ContextMenuStrip = $linMenu; $lin.Empty.ContextMenuStrip = $linMenu

# --- Worker operations ---
function Begin-Operation($Request) {
    if ($script:busy) {return}
    try {
        $id = [guid]::NewGuid().ToString('N')
        $jobs = Join-Path $script:Runtime 'jobs'
        New-Item -ItemType Directory -Path $jobs -Force | Out-Null
        $script:requestPath = Join-Path $jobs ($id + '.request.json')
        $script:responsePath = Join-Path $jobs ($id + '.response.json')
        Write-Utf8 $script:requestPath ($Request | ConvertTo-Json -Depth 6)
        $script:pendingAction = $Request.action
        $script:pendingRequest = $Request
        $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $script:Root 'scripts\Worker.ps1'),'-RequestFile',$script:requestPath,'-ResponseFile',$script:responsePath)
        $script:process = Start-Process 'powershell.exe' -ArgumentList (($arguments | ForEach-Object {Quote-Argument $_}) -join ' ') -WindowStyle Hidden -PassThru
        $script:opStarted = Get-Date
        if ($Request.action -in 'start','setup') {
            $script:bootStarted = Get-Date; $script:bootPhase = ''
            $script:bootEstimate = if ($Request.action -eq 'setup') {75} else {Get-BootEstimate}
            $bootProgress.Value = 0; $bootProgress.Visible = $true
        }
        if ($Request.action -in 'copy','fetch','transfer','download') {Start-TransferView $Request}
        Set-Busy $true
        if ($Request.action -notin $quietActions) {
            Set-Message ($actionNames[$Request.action] + '…')
            Log-Line ('開始：' + $actionNames[$Request.action])
        }
    } catch {Log-Line $_.Exception.Message; $script:followUp = $null; Set-Busy $false}
}

function Start-TransferView($Request) {
    $script:samples.Clear()
    $transferTitle.Text = switch ($Request.action) {
        'fetch' {if ($Request.saveTo) {('正在存到電腦：{0}' -f $Request.name)} else {('正在開啟：{0}' -f $Request.name)}}
        'transfer' {('正在傳輸 {0} 個項目：{1} → {2}' -f @($Request.paths).Count,(Get-VolumeLabel $Request.source),(Get-VolumeLabel $Request.device))}
        'download' {('正在複製 {0} 個項目到電腦：{1}' -f @($Request.paths).Count,$Request.destination)}
        default {
            if ($Request.overwrite) {('正在寫回隨身碟：{0}' -f $Request.targetName)}
            else {('正在複製 {0} 個項目到 {1}' -f @($Request.paths).Count,(Get-VolumeLabel $Request.device))}
        }
    }
    $transferFile.Text = '準備中…'
    $transferStats.Text = '正在計算剩餘時間…'
    $transferBar.Value = 0
    $transferGap.Visible = $true; $transfer.Visible = $true
}
function Update-TransferView {
    $file = $script:responsePath + '.progress'
    if (-not (Test-Path -LiteralPath $file)) {return}
    try {$p = Get-Content -Raw -Encoding UTF8 -LiteralPath $file | ConvertFrom-Json} catch {return}
    if (-not $p.total -or -not $p.stage) {return}
    $stage = switch ($p.stage) { 'upload' {'傳送到 Linux'} 'write' {'寫入隨身碟'} 'verify' {'驗證 SHA-256'} 'download' {'從隨身碟讀取'} default {'處理中'} }
    $which = if ([int]$p.count -gt 1) {('（第 {0} / {1} 個）' -f $p.index,$p.count)} else {''}
    $transferFile.Text = ('{0}：{1}{2}' -f $stage,$p.file,$which)
    $fraction = [Math]::Min(1.0,[double]$p.done / [double]$p.total)
    $transferBar.Value = [int](100 * $fraction)
    # Remaining time from the throughput over the last few seconds, which follows stage changes.
    $now = Get-Date
    $script:samples.Add(@{t=$now; done=[double]$p.done})
    while ($script:samples.Count -gt 2 -and ($now - $script:samples[0].t).TotalSeconds -gt 8) {$script:samples.RemoveAt(0)}
    $first = $script:samples[0]
    $span = ($now - $first.t).TotalSeconds
    $rate = if ($span -gt 0) {([double]$p.done - $first.done) / $span} else {0}
    if (($now - $script:opStarted).TotalSeconds -lt 2 -or $rate -le 0) {
        $transferStats.Text = ('{0:P0} · 正在計算剩餘時間…' -f $fraction)
    } else {
        $remaining = [int][Math]::Ceiling(([double]$p.total - [double]$p.done) / $rate)
        $transferStats.Text = ('{0:P0} · 剩餘約 {1} · 預計 {2} 完成' -f $fraction,(Format-Duration $remaining),$now.AddSeconds($remaining).ToString('HH:mm:ss'))
    }
}

# Boot time estimate: average of recent successful boots recorded by the worker.
function Get-BootEstimate {
    if ($script:vmState -eq 'running') {return 30}
    try {
        $history = @((Read-Config).bootSeconds | Where-Object {$_})
        if ($history.Count) {return [Math]::Max(15,[int](($history | Measure-Object -Average).Average))}
    } catch { }
    # The very first start installs packages offline and seals the image.
    return 120
}
function Update-BootProgress {
    $progressFile = $script:responsePath + '.progress'
    if (Test-Path -LiteralPath $progressFile) {
        $phase = ''
        try {$phase = (Get-Content -Raw -Encoding UTF8 -LiteralPath $progressFile | ConvertFrom-Json).phase} catch { }
        if ($phase -and $phase -ne $script:bootPhase) {
            # The estimate restarts for a power-cycled boot, an agent update or sealing the image.
            $script:bootPhase = $phase; $script:bootStarted = Get-Date
            if ($phase -eq 'retry') {Log-Line 'Linux 開機停滯，已自動重新啟動。'}
            if ($phase -eq 'updating') {$script:bootEstimate = [Math]::Max($script:bootEstimate,20); Log-Line '正在更新 Linux 內的元件。'}
            if ($phase -eq 'virtualbox') {$script:bootEstimate = 75; Log-Line '正在安裝 VirtualBox（請在系統管理員提示按「是」）。'}
            if ($phase -eq 'prepare') {$script:bootEstimate = 40; Log-Line '正在建立 Linux 虛擬機。'}
            if ($phase -eq 'sealing') {$script:bootEstimate = [Math]::Max($script:bootEstimate,40); Log-Line '正在保存乾淨的 Linux 映像（只需一次），之後每次啟動都從這個狀態開始。'}
        }
    }
    $elapsed = ((Get-Date) - $script:bootStarted).TotalSeconds
    $estimate = $script:bootEstimate
    $prefix = switch ($script:bootPhase) { 'virtualbox' {'正在安裝 VirtualBox'} 'prepare' {'正在建立 Linux'} 'retry' {'重新啟動中'} 'updating' {'正在更新元件'} 'sealing' {'正在保存乾淨映像'} default {'正在啟動'} }
    if ($elapsed -lt $estimate) {
        $bootProgress.Value = [int](90 * $elapsed / $estimate)
        $linuxDetail.Text = ('{0}… 預估剩餘 {1}' -f $prefix,(Format-Duration ([int][Math]::Ceiling($estimate - $elapsed))))
    } else {
        # Past the estimate: creep toward the end without claiming completion.
        $bootProgress.Value = [int](90 + 8 * (1 - [Math]::Exp(-($elapsed - $estimate) / 60)))
        $linuxDetail.Text = ('{0}… 比預期久，仍在等待（已 {1}）' -f $prefix,(Format-Duration ([int]$elapsed)))
    }
}

$deviceWatcher = New-Object UsbProxyUi.DeviceChangeWatcher($form)
$deviceWatcher.Add_Changed({ $script:lastDeviceChange = Get-Date; $script:refreshWanted = $true })

$timer = New-Object Windows.Forms.Timer
$timer.Interval = 250
$timer.Add_Tick({
    if (-not $script:busy) {
        if ($script:vmState -eq 'stopping') {
            # Nothing else changes this state; poll until the VM has actually powered off.
            if (((Get-Date) - $script:lastStoppingPoll).TotalSeconds -ge 5) {
                $script:lastStoppingPoll = Get-Date
                Begin-Operation @{action='status'}
            }
            return
        }
        # Devices settle for a moment after a plug or unplug before the lists are re-read.
        if ($script:refreshWanted -and ((Get-Date) - $script:lastDeviceChange).TotalSeconds -ge 1.5) {
            $script:refreshWanted = $false
            Begin-Operation @{action='status'}
            return
        }
        Watch-OpenFiles
        return
    }
    if (-not $script:process) {return}
    if (-not $script:process.HasExited) {
        if ($script:pendingAction -in 'start','setup') {Update-BootProgress}
        if ($script:pendingAction -in 'copy','fetch','transfer','download') {Update-TransferView}
        return
    }
    $succeeded = $false
    $action = $script:pendingAction
    $request = $script:pendingRequest
    try {
        if (-not (Test-Path $script:responsePath)) {throw '背景程序未產生結果，請查看 logs 資料夾。'}
        $response = Get-Content -Raw -Encoding UTF8 -LiteralPath $script:responsePath | ConvertFrom-Json
        if (-not $response.ok) {throw $response.error}
        $data = $response.data
        switch ($action) {
            'status' {
                $script:usb = @($data.usb | Where-Object {$_})
                $script:vmState = [string]$data.vm.state
                $script:attached = @($data.vm.attached | Where-Object {$_ -and $_.uuid})
                $script:ready = $false
                Update-Guest $data.guest
                if ($data.message) {Log-Line ('Linux 尚未就緒或連線失敗：' + $data.message)}
                if ($script:switchTarget -and $script:ready) {
                    $script:followUp = @{action='attach'; uuid=$script:switchTarget.uuid; name=$script:switchTarget.name}
                } elseif ($script:switchTarget) {
                    Log-Line 'Linux 尚未就緒，無法切換隨身碟。'
                    $script:switchTarget = $null
                }
                if ($win.Location.kind -eq 'pc') {Show-WinComputer}
                # A Linux folder whose volume disappeared falls back to the Linux root.
                if ($lin.Location.kind -eq 'folder' -and -not @($script:volumes | Where-Object {$_.path -eq $lin.Location.device}).Count) {Reset-Lin}
                elseif ($lin.Location.kind -eq 'pc') {Show-LinComputer}
                # On a machine without the Linux environment, offer to install it once per session.
                if ($script:vmState -eq 'notinstalled' -and -not $script:setupOffered) {$script:setupOffered = $true; $script:followUp = @{offerSetup=$true}}
            }
            'setup' {
                Log-Line ('安裝完成，耗時 ' + (Format-Duration ([int]((Get-Date) - $script:opStarted).TotalSeconds)) + '。接著第一次啟動 Linux。')
                $script:vmState = 'poweroff'
                $script:followUp = @{action='start'}
            }
            'start' {
                $script:vmState = 'running'; $script:ready = $true
                Log-Line ('Linux 啟動耗時 ' + (Format-Duration ([int]((Get-Date) - $script:bootStarted).TotalSeconds)))
                if ($data.agentUpdated) {Log-Line 'Linux 內的元件已更新。'}
                $script:followUp = @{action='status'}
            }
            'attach' {
                Update-Guest $data.guest
                $script:switchTarget = $null
                $new = @($data.volumes | Where-Object {$_})
                # Open the drive's only partition; otherwise show every drive in Linux.
                if ($new.Count -eq 1) {
                    $lin.History.Clear(); $lin.Location = @{kind='pc'}
                    $script:followUp = @{nav=@{kind='folder'; device=[string]$new[0]; path=''}; push=$true; thenStatus=$true}
                } else {$script:followUp = @{action='status'}}
            }
            'list' {
                $nav = $script:pendingNav; $script:pendingNav = $null
                $target = @{kind='folder'; device=[string]$request.device; path=[string]$data.path}
                if ($nav -and $nav.push -and -not (Test-SameLocation $lin.Location $target)) {$lin.History.Add($lin.Location)}
                $lin.Location = $target
                Show-LinFolder $data ([string]$request.device)
            }
            'copy' {
                if ($request.overwrite) {Log-Line ('已寫回「{0}」，SHA-256 驗證通過。' -f $request.targetName)}
                else {Log-Line ('已複製並驗證 {0} 個檔案，共 {1}。' -f @($data.copied).Count,(Format-Size $data.bytes))}
            }
            'transfer' {Log-Line ('隨身碟間傳輸完成：{0} 個檔案，共 {1}，SHA-256 驗證通過。' -f @($data.copied).Count,(Format-Size $data.bytes))}
            'download' {Log-Line ('已複製 {0} 個檔案（{1}）到 {2}，SHA-256 驗證通過。' -f $data.downloaded,(Format-Size $data.bytes),$data.destination)}
            'fetch' {
                if ($data.saved) {Log-Line ('已存到 ' + $data.saved)}
                else {
                    $local = [string]$data.local
                    $script:openFiles.Add([pscustomobject]@{Local=$local; Remote=[string]$data.path; Name=[string]$data.name
                        Device=[string]$request.device; Stamp=(Get-Item -LiteralPath $local).LastWriteTimeUtc; Seen=$null})
                    try {Start-Process -FilePath $local}
                    catch {[Windows.Forms.MessageBox]::Show('沒有可開啟這個檔案的程式。','USB Proxy') | Out-Null}
                }
            }
            'eject' {$script:followUp = @{action='status'}}
            'shutdown' {
                $script:vmState = if ($data.off) {'poweroff'} else {'stopping'}
                $script:ready = $false
                if ($data.ejected) {$script:attached = @(); Update-Guest $null; $script:openFiles.Clear(); Reset-Lin; Log-Line '已自動退出所有隨身碟。'}
                $script:followUp = @{action='status'}
            }
        }
        if ($action -notin $quietActions) {
            Log-Line ('完成：' + $actionNames[$action])
            $done = switch ($action) {
                'copy' {if ($request.overwrite) {'已寫回隨身碟，SHA-256 驗證通過'} else {'複製完成，SHA-256 驗證通過'}}
                'transfer' {'隨身碟間傳輸完成，SHA-256 驗證通過'}
                'download' {'已複製到電腦，SHA-256 驗證通過'}
                'fetch' {if ($data.saved) {'已存到電腦'} else {'已開啟；修改並儲存後，可寫回隨身碟'}}
                'eject' {'已退出，可以安全拔除隨身碟'}
                'shutdown' {if ($data.off) {'Linux 已關閉'} else {'Linux 約一分鐘內關機'}}
                default {$actionNames[$action] + '完成'}
            }
            Set-Message $done
        }
        $succeeded = $true
    } catch {
        $reason = $_.Exception.Message
        Log-Line ('失敗：' + $actionNames[$action] + ' — ' + $reason)
        Set-Message ('操作失敗：' + $reason)
        $script:switchTarget = $null; $script:pendingNav = $null; $script:editAfter = $null
        # A failed start still needs a status refresh; writes re-read the folder they touched.
        $script:followUp = if ($action -eq 'start') {@{action='status'}}
            elseif ($action -in 'delete','rename','mkdir','copy','transfer' -and $lin.Location.kind -eq 'folder') {@{nav=$lin.Location}}
            elseif ($action -eq 'download') {@{refreshWin=$true}} else {$null}
        if ($action -notin $quietActions) {[Windows.Forms.MessageBox]::Show(($actionNames[$action] + '失敗：' + "`n`n" + $reason),'USB Proxy',[Windows.Forms.MessageBoxButtons]::OK,[Windows.Forms.MessageBoxIcon]::Warning) | Out-Null}
    }
    finally {$script:process.Dispose(); $script:process = $null; Set-Busy $false}
    if ($script:closeWhenDone) {
        $script:closeWhenDone = $false
        if ($succeeded -and $action -eq 'shutdown') {$form.Close(); return}
    }
    if ($script:followUp) {
        $next = $script:followUp; $script:followUp = $null
        if ($next.offerSetup) {Start-Setup}
        elseif ($next.refreshWin) {if ($win.Location.kind -eq 'folder') {Navigate $win $win.Location -NoHistory}}
        elseif ($next.nav) {
            if ($next.thenStatus) {$script:refreshWanted = $true; $script:lastDeviceChange = [datetime]::MinValue}
            if ($next.nav.kind -ne 'folder') {Show-LinComputer} elseif ($next.push) {Navigate $lin $next.nav} else {Navigate $lin $next.nav -NoHistory}
        } else {Begin-Operation $next}
    }
    Update-Counts
})

$form.Add_FormClosing({
    param($sender,$event)
    if ($script:busy) {$event.Cancel=$true; [Windows.Forms.MessageBox]::Show('工作進行中，請等待完成再關閉。','USB Proxy') | Out-Null; return}
    if ($script:attached.Count) {
        $event.Cancel = $true
        $choice = [Windows.Forms.MessageBox]::Show('仍有隨身碟在 Linux 中。要安全退出、關閉 Linux 後再結束嗎？','USB Proxy',[Windows.Forms.MessageBoxButtons]::OKCancel,[Windows.Forms.MessageBoxIcon]::Question)
        if ($choice -eq [Windows.Forms.DialogResult]::OK) {$script:closeWhenDone = $true; Begin-Operation @{action='shutdown'}}
        return
    }
    # Remember the views, split and window size for next time.
    try {
        $bounds = if ($form.WindowState -eq 'Normal') {$form.ClientSize} else {$form.RestoreBounds.Size}
        $ui.width = [int]($bounds.Width / $scale); $ui.height = [int]($bounds.Height / $scale)
        $ui.maximized = $form.WindowState -eq 'Maximized'
        $ui.split = [Math]::Round($split.SplitterDistance / [double][Math]::Max(1,$split.Width),3)
        Write-Utf8 $uiFile ($ui | ConvertTo-Json)
    } catch { }
})
$form.Add_Shown({
    [UsbProxyUi.Native]::StyleWindow($form.Handle,[UsbProxyUi.Theme]::Background,[UsbProxyUi.Theme]::Label)
    if ($ui.maximized) {$form.WindowState = 'Maximized'}
    $ratio = [double]$ui.split
    if ($ratio -gt 0.2 -and $ratio -lt 0.8) {$split.SplitterDistance = [int]($split.Width * $ratio)}
})
if ([int]$ui.width -ge 900 -and [int]$ui.height -ge 560) {$form.ClientSize = New-Object Drawing.Size([int]$ui.width,[int]$ui.height)}
# Local copies of files opened in an earlier session; files still open elsewhere are skipped.
try { Get-ChildItem -LiteralPath (Join-Path $script:Runtime 'open') -Directory -ErrorAction Stop | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue } catch { }
Show-WinComputer
Show-LinComputer
Update-State
if (-not $SmokeTest) { $form.Add_Shown({$timer.Start(); Begin-Operation @{action='status'}}) }
if ($SmokeTest) {
    Write-Output ('UI constructed: ' + $form.Controls.Count + ' top-level controls')
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    $bitmap = New-Object Drawing.Bitmap($form.Width,$form.Height)
    $form.DrawToBitmap($bitmap,(New-Object Drawing.Rectangle(0,0,$form.Width,$form.Height)))
    $bitmap.Save((Join-Path $script:Root 'logs\ui-preview.png'),[Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose(); $timer.Dispose(); $form.Dispose()
    return
}
try {[Windows.Forms.Application]::Run($form)} finally {$timer.Stop(); $timer.Dispose(); $form.Dispose()}
