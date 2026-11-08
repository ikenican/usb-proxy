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
$script:attached = $false
$script:attachedName = ''
$script:ready = $false
$script:vmState = 'unknown'
$script:closeWhenDone = $false
$script:lastStoppingPoll = [datetime]::MinValue
# Where the browser is: 本機 (pc) or a folder on a volume; Back returns through history.
$script:location = @{kind='pc'}
$script:history = New-Object 'System.Collections.Generic.List[object]'
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
$actionNames = @{status='重新整理'; start='啟動 Linux'; shutdown='關閉 Linux'; attach='切換到 Linux'
    eject='退出隨身碟'; copy='複製'; list='讀取資料夾'; delete='刪除'; mkdir='新增資料夾'; rename='重新命名'; fetch='讀取檔案'}
$quietActions = @('status','list')
$uiFile = Join-Path $script:Runtime 'ui.json'
$ui = @{zoom=0; iconStep=2; width=980; height=700; maximized=$false}
try { $saved = Get-Content -Raw -Encoding UTF8 -LiteralPath $uiFile | ConvertFrom-Json; foreach ($key in @($ui.Keys)) { if ($null -ne $saved.$key) { $ui[$key] = $saved.$key } } } catch { }

$fontFamily = 'Microsoft JhengHei UI'
$bodyFont = New-Object Drawing.Font($fontFamily,9.75)
$headlineFont = New-Object Drawing.Font($fontFamily,11,[Drawing.FontStyle]::Bold)
$captionFont = New-Object Drawing.Font($fontFamily,9)
$glyphFont = New-Object Drawing.Font('Segoe UI Symbol',11)

$form = New-Object Windows.Forms.Form
$form.AutoScaleDimensions = New-Object Drawing.SizeF(96,96)
$form.AutoScaleMode = 'Dpi'
$form.Text = 'USB Proxy'
$form.Icon = New-Object Drawing.Icon("$PSScriptRoot\app.ico")
$form.ClientSize = New-Object Drawing.Size(980,700)
$form.MinimumSize = New-Object Drawing.Size(760,540)
$form.BackColor = [UsbProxyUi.Theme]::Background
$form.ForeColor = [UsbProxyUi.Theme]::Label
$form.Font = $bodyFont
$form.StartPosition = 'CenterScreen'
$form.AllowDrop = $true
$W = 980; $H = 700
$penguin = [Drawing.Image]::FromFile("$PSScriptRoot\penguin.png")

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

# Linux status bar
$linuxCard = New-Object UsbProxyUi.Card
$linuxCard.Location = New-Object Drawing.Point(16,12); $linuxCard.Size = New-Object Drawing.Size(($W-32),64); $linuxCard.Anchor = 'Top,Left,Right'
$form.Controls.Add($linuxCard)
$dot = New-Object UsbProxyUi.PenguinBadge
$dot.Location = New-Object Drawing.Point(14,9); $dot.Size = New-Object Drawing.Size(38,44)
$linuxCard.Controls.Add($dot)
$linuxTitle = New-Text $linuxCard 'Linux' 62 9 500 22 $headlineFont
$linuxDetail = New-Text $linuxCard '正在檢查狀態…' 62 33 ($W-32-62-170) 20 $captionFont ([UsbProxyUi.Theme]::SecondaryLabel) 'Top,Left,Right'
$bootProgress = New-Object UsbProxyUi.ThinProgress
$bootProgress.Location = New-Object Drawing.Point(62,55); $bootProgress.Size = New-Object Drawing.Size(($W-32-62-170),5); $bootProgress.Anchor = 'Top,Left,Right'
$bootProgress.Visible = $false
$linuxCard.Controls.Add($bootProgress)
$linuxToggle = New-Button $linuxCard '啟動 Linux' 'Primary' ($W-32-14-128) 15 128 {
    if ($script:vmState -eq 'running') {
        if ($script:attached) {
            $choice = [Windows.Forms.MessageBox]::Show('關閉 Linux 前會先安全退出隨身碟並交還 Windows。請確認沒有正在進行的複製。','關閉 Linux',[Windows.Forms.MessageBoxButtons]::OKCancel,[Windows.Forms.MessageBoxIcon]::Information)
            if ($choice -ne [Windows.Forms.DialogResult]::OK) {return}
        }
        Begin-Operation @{action='shutdown'}
    } else { Begin-Operation @{action='start'} }
} 34 'Top,Right'

# Toolbar: back, up, address, view and actions
$backButton = New-Button $form '←' 'Secondary' 16 88 38 { Go-Back }
$backButton.Font = $glyphFont
$upButton = New-Button $form '↑' 'Secondary' 58 88 38 { Go-Up }
$upButton.Font = $glyphFont
$address = New-Object UsbProxyUi.Card
$address.Location = New-Object Drawing.Point(104,88); $address.Size = New-Object Drawing.Size(($W-104-16-370),32); $address.Anchor = 'Top,Left,Right'
$form.Controls.Add($address)
$addressText = New-Text $address '本機' 12 6 ($W-104-16-370-24) 20 $bodyFont ([UsbProxyUi.Theme]::Label) 'Top,Left,Right'
$viewPopup = New-Object UsbProxyUi.PopupSelect
$viewPopup.Location = New-Object Drawing.Point(($W-16-362),88); $viewPopup.Size = New-Object Drawing.Size(116,32); $viewPopup.Anchor = 'Top,Right'
$viewPopup.SetItems([string[]]@('大圖示','清單','詳細資料'))
$form.Controls.Add($viewPopup)
$newFolderButton = New-Button $form '新增資料夾' 'Plain' ($W-16-240) 88 92 { New-RemoteFolder } 32 'Top,Right'
$addButton = New-Button $form '加入…' 'Plain' ($W-16-144) 88 66 { Add-FromDialog } 32 'Top,Right'
$ejectButton = New-Button $form '退出' 'Plain' ($W-16-74) 88 74 { Eject-Drive } 32 'Top,Right'

# Main browser
$mainCard = New-Object UsbProxyUi.Card
$mainCard.Location = New-Object Drawing.Point(16,128); $mainCard.Size = New-Object Drawing.Size(($W-32),($H-128-40)); $mainCard.Anchor = 'Top,Bottom,Left,Right'
$mainCard.Padding = New-Object Windows.Forms.Padding(6)
$form.Controls.Add($mainCard)
$list = New-Object UsbProxyUi.ExplorerListView
$list.Dock = 'Fill'; $list.BackColor = [UsbProxyUi.Theme]::Surface; $list.Font = $bodyFont
$scale = $form.DeviceDpi / 96
$largeIcons = New-Object Windows.Forms.ImageList; $largeIcons.ColorDepth = 'Depth32Bit'; $largeIcons.ImageSize = New-Object Drawing.Size([int](48*$scale),[int](48*$scale))
$smallIcons = New-Object Windows.Forms.ImageList; $smallIcons.ColorDepth = 'Depth32Bit'; $smallIcons.ImageSize = New-Object Drawing.Size([int](16*$scale),[int](16*$scale))
$list.LargeImageList = $largeIcons; $list.SmallImageList = $smallIcons
$mainCard.Controls.Add($list)
$emptyLabel = New-Object Windows.Forms.Label
$emptyLabel.Dock = 'Fill'; $emptyLabel.TextAlign = 'MiddleCenter'; $emptyLabel.ForeColor = [UsbProxyUi.Theme]::SecondaryLabel
$emptyLabel.BackColor = [UsbProxyUi.Theme]::Surface; $emptyLabel.AllowDrop = $true; $emptyLabel.Visible = $false
$list.Controls.Add($emptyLabel)

# Transfer panel: current file, progress, remaining time
$transfer = New-Object Windows.Forms.Panel
# Size the panel before anchoring children so they track its real width.
$transfer.Size = New-Object Drawing.Size(($W-32-12),92)
$transfer.Dock = 'Bottom'; $transfer.BackColor = [UsbProxyUi.Theme]::Surface; $transfer.Visible = $false
$mainCard.Controls.Add($transfer)
$rule = New-Object Windows.Forms.Panel
$rule.Dock = 'Top'; $rule.Height = 1; $rule.BackColor = [UsbProxyUi.Theme]::Separator
$transfer.Controls.Add($rule)
$transferTitle = New-Text $transfer '' 12 10 ($W-32-36) 22 $headlineFont ([UsbProxyUi.Theme]::Label) 'Top,Left,Right'
$transferFile = New-Text $transfer '' 12 34 ($W-32-36) 20 $captionFont ([UsbProxyUi.Theme]::SecondaryLabel) 'Top,Left,Right'
$transferBar = New-Object UsbProxyUi.ThinProgress
$transferBar.Location = New-Object Drawing.Point(12,60); $transferBar.Size = New-Object Drawing.Size(($W-32-36),6); $transferBar.Anchor = 'Top,Left,Right'
$transfer.Controls.Add($transferBar)
$transferStats = New-Text $transfer '' 12 68 ($W-32-36) 20 $captionFont ([UsbProxyUi.Theme]::SecondaryLabel) 'Top,Left,Right'

# Status bar
$statusText = New-Text $form '' 18 ($H-30) ($W-36-110) 22 $captionFont ([UsbProxyUi.Theme]::SecondaryLabel) 'Bottom,Left,Right'
$logButton = New-Button $form '活動記錄' 'Plain' ($W-16-96) ($H-34) 96 { $logWindow.Show(); $logWindow.Activate() } 28 'Bottom,Right'

$logWindow = New-Object Windows.Forms.Form
$logWindow.Text = '活動記錄'; $logWindow.Icon = $form.Icon; $logWindow.ShowInTaskbar = $false
$logWindow.Size = New-Object Drawing.Size(640,360); $logWindow.StartPosition = 'CenterParent'; $logWindow.Owner = $form
$log = New-Object Windows.Forms.TextBox
$log.Multiline=$true; $log.ReadOnly=$true; $log.ScrollBars='Vertical'; $log.Dock='Fill'; $log.BorderStyle='None'
$log.BackColor=[UsbProxyUi.Theme]::Surface; $log.Font=$captionFont
$logWindow.Controls.Add($log)
$logWindow.Add_FormClosing({ param($s,$e) if ($e.CloseReason -eq 'UserClosing') { $e.Cancel=$true; $logWindow.Hide() } })

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
    $index=$Path.LastIndexOf('/')
    if ($index -lt 0) {return ''} else {return $Path.Substring(0,$index)}
}
function Join-RemotePath([string]$Folder,[string]$Name) { if ($Folder) {"$Folder/$Name"} else {$Name} }
function Get-SelectedEntries { @($list.SelectedItems | ForEach-Object {$_.Tag}) }
function Get-VolumeName([string]$Device) {
    $v = @($script:volumes | Where-Object {$_.path -eq $Device})
    if ($v.Count -and $v[0].label) {return [string]$v[0].label}
    return 'USB 隨身碟'
}

# Icons come from the Windows shell, cached per extension in both image lists.
function Get-IconKey($Entry) {
    switch ($Entry.Kind) {
        'device' {$key='~device'}
        {$_ -in 'volume','linux-device'} {$key='~volume'}
        'folder' {$key='~folder'}
        default {$ext=[IO.Path]::GetExtension($Entry.Name).ToLowerInvariant(); $key = if ($ext) {$ext} else {'~file'}}
    }
    foreach ($images in @($largeIcons,$smallIcons)) {
        if ($images.Images.ContainsKey($key)) {continue}
        $size = $images.ImageSize.Width
        $image = switch ($key) {
            '~device' {[UsbProxyUi.ShellIcons]::Stock([UsbProxyUi.ShellIcons]::DriveRemovable,$size)}
            '~volume' {[UsbProxyUi.ShellIcons]::WithBadge([UsbProxyUi.ShellIcons]::Stock([UsbProxyUi.ShellIcons]::DriveRemovable,$size),$penguin)}
            '~folder' {[UsbProxyUi.ShellIcons]::ForFile('folder',$true,$size)}
            '~file' {[UsbProxyUi.ShellIcons]::ForFile('file',$false,$size)}
            default {[UsbProxyUi.ShellIcons]::ForFile('file' + $key,$false,$size)}
        }
        $images.Images.Add($key,$image)
    }
    return $key
}

# Zoom steps as in Explorer (Ctrl + mouse wheel): details, list, then icons of growing size.
$zoomSizes = @(0,0,48,64,96,128,192,256)
$script:settingView = $false
function Set-Zoom([int]$Step) {
    $Step = [Math]::Max(0,[Math]::Min($zoomSizes.Count-1,$Step))
    $ui.zoom = $Step
    if ($Step -ge 2) {
        $ui.iconStep = $Step
        # Image lists hold at most 256 px images.
        $px = [Math]::Min(256,[int]($zoomSizes[$Step]*$scale))
        if ($largeIcons.ImageSize.Width -ne $px) {
            $list.BeginUpdate()
            $largeIcons.Images.Clear()
            $largeIcons.ImageSize = New-Object Drawing.Size($px,$px)
            foreach ($item in $list.Items) {$null = Get-IconKey $item.Tag}
            $list.LargeImageList = $null; $list.LargeImageList = $largeIcons
            $list.EndUpdate()
        }
        $list.SetIconSpacing([int]($px + 44*$scale),[int]($px + 48*$scale))
        $list.View = 'LargeIcon'
    } elseif ($Step -eq 1) {$list.View = 'List'}
    else {$list.View = 'Details'}
    $script:settingView = $true
    $viewPopup.SelectedIndex = if ($Step -ge 2) {0} elseif ($Step -eq 1) {1} else {2}
    $script:settingView = $false
}
function Add-ListItem($Entry,[string]$Column1,[string]$Column3,[string]$Group='') {
    $item = New-Object Windows.Forms.ListViewItem($Entry.Name,(Get-IconKey $Entry))
    $null = $item.SubItems.Add($Column1)
    $null = $item.SubItems.Add([string]$Entry.TypeName)
    $null = $item.SubItems.Add($Column3)
    $item.Tag = $Entry
    if ($Group) {$item.Group = $list.Groups[$Group]}
    $script:pendingItems.Add($item)
}
# Adds collected items in one batch; the sorter is detached meanwhile so it runs once.
function Commit-Items {
    $sorter = $list.ListViewItemSorter
    $list.ListViewItemSorter = $null
    $list.Items.AddRange($script:pendingItems.ToArray())
    $script:pendingItems.Clear()
    $list.ListViewItemSorter = $sorter
}
$script:pendingItems = New-Object 'System.Collections.Generic.List[System.Windows.Forms.ListViewItem]'
function Set-Columns([string[]]$Names,[int[]]$Widths) {
    $list.Columns.Clear()
    for ($i=0;$i -lt $Names.Count;$i++) {
        $column = $list.Columns.Add($Names[$i],[int]($Widths[$i]*$scale))
        if ($i -eq 3) {$column.TextAlign='Right'}
    }
}
function Set-Empty([string]$Text) { $emptyLabel.Text=$Text; $emptyLabel.Visible=[bool]$Text }

# 本機: drives in Windows (double-click to switch) and drives in Linux.
function Show-Computer {
    $list.BeginUpdate()
    $list.Items.Clear(); $list.Groups.Clear()
    $list.FoldersFirst = $false; $list.LabelEdit = $false
    Set-Columns @('名稱','位置','類型','總大小') @(320,120,240,110)
    $null = $list.Groups.Add('linux','在 Linux 中')
    $null = $list.Groups.Add('windows','在 Windows 中（按兩下切換到 Linux）')
    if ($script:attached) {
        if ($script:volumes.Count) {
            foreach ($v in $script:volumes) {
                $entry = New-Object UsbProxyUi.FileEntry
                $label = if ($v.label) {$v.label} else {'USB 隨身碟'}
                $entry.Name = ('{0} ({1})' -f $label,([string]$v.fstype).ToUpper()); $entry.Kind='volume'; $entry.Device=[string]$v.path
                $entry.Size=[long]$v.size; $entry.TypeName='USB 隨身碟 · Linux'
                Add-ListItem $entry 'Linux' (Format-Size $v.size) 'linux'
            }
        } else {
            $entry = New-Object UsbProxyUi.FileEntry
            $entry.Name = if ($script:attachedName) {$script:attachedName} else {'USB 隨身碟'}
            $entry.Kind='linux-device'; $entry.TypeName='找不到支援的分割區'
            Add-ListItem $entry 'Linux' '' 'linux'
        }
    }
    foreach ($d in $script:usb) {
        $entry = New-Object UsbProxyUi.FileEntry
        $name = (($d.Manufacturer,$d.Product) | Where-Object {$_ -and $_.Trim()} | ForEach-Object {$_.Trim()}) -join ' '
        $entry.Name = if ($name) {$name} else {'USB 隨身碟'}
        $entry.Kind='device'; $entry.Uuid=[string]$d.uuid; $entry.TypeName=('USB 隨身碟 · 連接埠 ' + $d.Port)
        Add-ListItem $entry 'Windows' '' 'windows'
    }
    Commit-Items
    $list.ShowGroups = $true
    $list.EndUpdate()
    $count = $list.Items.Count
    Set-Empty $(if ($count) {''} else {"沒有偵測到隨身碟`n`n插入隨身碟後會自動顯示在這裡"})
    $addressText.Text = '本機'
    Update-Counts
    Update-State
}

function Show-Folder($Data,[string]$Device) {
    $list.BeginUpdate()
    $list.Items.Clear(); $list.Groups.Clear(); $list.ShowGroups = $false
    $list.FoldersFirst = $true; $list.LabelEdit = $true
    Set-Columns @('名稱','修改日期','類型','大小') @(340,160,200,100)
    foreach ($item in @($Data.entries | Where-Object {$_})) {
        $entry = New-Object UsbProxyUi.FileEntry
        $entry.Name=[string]$item.name; $entry.Path=Join-RemotePath ([string]$Data.path) $item.name; $entry.Device=$Device
        $entry.Kind = if ($item.link) {'link'} elseif ($item.dir) {'folder'} else {'file'}
        $entry.Size=[long]$item.size
        $entry.Modified=[DateTimeOffset]::FromUnixTimeSeconds([long]$item.mtime).LocalDateTime
        $entry.TypeName = if ($entry.IsLink) {'符號連結（不支援）'} else {[UsbProxyUi.ShellIcons]::TypeName($entry.Name,$entry.IsFolder)}
        $sizeText = if ($entry.Kind -eq 'file') {Format-Size $entry.Size} else {''}
        Add-ListItem $entry ($entry.Modified.ToString('yyyy/MM/dd HH:mm')) $sizeText
    }
    Commit-Items
    $list.EndUpdate()
    Set-Empty $(if ($list.Items.Count) {''} else {"這個資料夾是空的`n`n把檔案或資料夾拖放到這裡即可複製到隨身碟"})
    $script:volumeInfo[$Device] = @{free=[long]$Data.free; total=[long]$Data.total}
    $crumbs = @('本機',(Get-VolumeName $Device)) + @(([string]$Data.path) -split '/' | Where-Object {$_})
    $addressText.Text = $crumbs -join '  ›  '
    if ($script:editAfter) {
        foreach ($item in $list.Items) {
            if ($item.Text -eq $script:editAfter) {$list.SelectedItems.Clear(); $item.Selected=$true; $item.Focused=$true; $item.EnsureVisible(); $item.BeginEdit()}
        }
        $script:editAfter = $null
    }
    Update-Counts
    Update-State
}

function Update-Counts {
    $parts = @(('{0} 個項目' -f $list.Items.Count))
    if ($list.SelectedItems.Count) {$parts += ('已選取 {0} 個' -f $list.SelectedItems.Count)}
    if ($script:location.kind -eq 'folder' -and $script:volumeInfo.ContainsKey($script:location.device)) {
        $v = $script:volumeInfo[$script:location.device]
        $parts += ('可用 {0}，共 {1}' -f (Format-Size $v.free),(Format-Size $v.total))
    }
    Set-Message ($parts -join '  ·  ')
}

function Update-State {
    $idle = -not $script:busy
    $running = $script:vmState -eq 'running'
    $starting = $script:busy -and $script:pendingAction -eq 'start'
    switch ($script:vmState) {
        'running' {
            if ($script:ready) {$linuxTitle.Text='Linux 已就緒'; $dot.BadgeColor=[UsbProxyUi.Theme]::Green}
            else {$linuxTitle.Text='Linux 啟動中'; $dot.BadgeColor=[UsbProxyUi.Theme]::Orange}
        }
        'stopping' {$linuxTitle.Text='Linux 正在關機'; $dot.BadgeColor=[UsbProxyUi.Theme]::Orange}
        'poweroff' {$linuxTitle.Text='Linux 未啟動'; $dot.BadgeColor=[UsbProxyUi.Theme]::Gray}
        'unknown' {$linuxTitle.Text='正在連線 Linux…'; $dot.BadgeColor=[UsbProxyUi.Theme]::Gray}
        default {$linuxTitle.Text='Linux 狀態：' + $script:vmState; $dot.BadgeColor=[UsbProxyUi.Theme]::Gray}
    }
    if ($starting) {$linuxTitle.Text='Linux 啟動中'; $dot.BadgeColor=[UsbProxyUi.Theme]::Orange}
    $dot.Dimmed = -not $running -and -not $starting
    if (-not $starting) {
        $linuxDetail.Text = if ($script:attached) {('「{0}」在 Linux 中，Windows 暫時無法使用' -f $(if ($script:attachedName) {$script:attachedName} else {'隨身碟'}))}
            elseif ($running) {'在「本機」按兩下隨身碟，即可切換到 Linux 管理'}
            elseif ($script:vmState -eq 'poweroff') {'按兩下隨身碟時會自動啟動'}
            else {$linuxDetail.Text}
    }
    if ($running) {$linuxToggle.Text='關閉 Linux'; $linuxToggle.Kind='Secondary'}
    else {$linuxToggle.Text='啟動 Linux'; $linuxToggle.Kind='Primary'}
    $linuxToggle.Enabled = $idle -and $script:vmState -ne 'stopping'
    $inFolder = $script:location.kind -eq 'folder'
    $backButton.Enabled = $idle -and $script:history.Count -gt 0
    $upButton.Enabled = $idle -and $inFolder
    $newFolderButton.Enabled = $idle -and $inFolder
    $addButton.Enabled = $idle -and $inFolder
    $ejectButton.Visible = $script:attached
    $ejectButton.Enabled = $idle
}

function Navigate([hashtable]$Target,[switch]$NoHistory) {
    if ($script:busy) {return}
    if ($Target.kind -eq 'pc') {
        if (-not $NoHistory -and $script:location.kind -ne 'pc') {$script:history.Add($script:location)}
        $script:location = @{kind='pc'}
        Show-Computer
        return
    }
    $script:pendingNav = @{target=$Target; push=(-not $NoHistory)}
    Begin-Operation @{action='list'; device=$Target.device; path=$Target.path}
}
function Go-Back {
    if ($script:busy -or $script:history.Count -eq 0) {return}
    $target = $script:history[$script:history.Count-1]
    $script:history.RemoveAt($script:history.Count-1)
    Navigate $target -NoHistory
}
function Go-Up {
    if ($script:location.kind -ne 'folder') {return}
    if ($script:location.path) {Navigate @{kind='folder'; device=$script:location.device; path=(Get-ParentPath $script:location.path)}}
    else {Navigate @{kind='pc'}}
}
function Refresh-View {
    if ($script:location.kind -eq 'folder') {Navigate $script:location -NoHistory}
    else {Begin-Operation @{action='status'}}
}
function Reset-ToComputer {
    $script:history.Clear()
    $script:location = @{kind='pc'}
    $script:openFiles.Clear()
    Show-Computer
}

function Switch-ToLinux($Entry) {
    if ($script:busy) {return}
    if ($script:attached) {
        [Windows.Forms.MessageBox]::Show('已有隨身碟在 Linux 中。請先「退出」它，再切換另一個。','切換到 Linux') | Out-Null
        return
    }
    $message = ('要將「{0}」切換到 Linux 嗎？' -f $Entry.Name) + "`n`n" + '切換後 Windows 會暫時無法使用這個隨身碟，請先關閉其中開啟的檔案。完成後按「退出」即可交還 Windows。'
    if ($script:vmState -ne 'running') {$message += "`n`n" + 'Linux 尚未啟動，會先自動啟動。'}
    $choice = [Windows.Forms.MessageBox]::Show($message,'切換到 Linux',[Windows.Forms.MessageBoxButtons]::OKCancel,[Windows.Forms.MessageBoxIcon]::Question)
    if ($choice -ne [Windows.Forms.DialogResult]::OK) {return}
    $script:switchTarget = @{uuid=$Entry.Uuid; name=$Entry.Name}
    if ($script:vmState -eq 'running' -and $script:ready) {Begin-Operation @{action='attach'; uuid=$Entry.Uuid; name=$Entry.Name}}
    else {Begin-Operation @{action='start'}}
}
function Eject-Drive {
    if ($script:busy -or -not $script:attached) {return}
    Begin-Operation @{action='eject'}
}

function Open-Selected {
    $selected = @(Get-SelectedEntries)
    if ($script:busy -or $selected.Count -ne 1) {return}
    $entry = $selected[0]
    switch ($entry.Kind) {
        'device' {Switch-ToLinux $entry}
        'volume' {Navigate @{kind='folder'; device=$entry.Device; path=''}}
        'linux-device' {[Windows.Forms.MessageBox]::Show('這個隨身碟沒有支援的分割區（FAT32、exFAT、NTFS、ext4）。','USB Proxy') | Out-Null}
        'folder' {Navigate @{kind='folder'; device=$entry.Device; path=$entry.Path}}
        'link' {[Windows.Forms.MessageBox]::Show('不支援開啟符號連結。','USB Proxy') | Out-Null}
        default {Begin-Operation @{action='fetch'; device=$entry.Device; path=$entry.Path; size=$entry.Size; name=$entry.Name}}
    }
}
function Save-Selected {
    $selected = @(Get-SelectedEntries)
    if ($script:busy -or $selected.Count -ne 1 -or $selected[0].Kind -ne 'file') {return}
    $dialog = New-Object Windows.Forms.SaveFileDialog
    $dialog.FileName = $selected[0].Name; $dialog.OverwritePrompt = $true
    if ($dialog.ShowDialog() -eq 'OK') {
        Begin-Operation @{action='fetch'; device=$selected[0].Device; path=$selected[0].Path; size=$selected[0].Size; name=$selected[0].Name; saveTo=$dialog.FileName}
    }
    $dialog.Dispose()
}
function Remove-Selected {
    $selected = @(Get-SelectedEntries | Where-Object {$_.Kind -in 'file','folder','link'})
    if ($script:busy -or $selected.Count -eq 0) {return}
    $what = if ($selected.Count -eq 1) {'「' + $selected[0].Name + '」'} else {('這 {0} 個項目' -f $selected.Count)}
    $choice = [Windows.Forms.MessageBox]::Show(('確定要刪除{0}嗎？' -f $what),'刪除',[Windows.Forms.MessageBoxButtons]::OKCancel,[Windows.Forms.MessageBoxIcon]::Warning,[Windows.Forms.MessageBoxDefaultButton]::Button2)
    if ($choice -ne [Windows.Forms.DialogResult]::OK) {return}
    $script:followUp = @{nav=$script:location}
    Begin-Operation @{action='delete'; device=$script:location.device; paths=@($selected | ForEach-Object {$_.Path})}
}
function Rename-Selected {
    $items = @($list.SelectedItems)
    if ($script:busy -or $items.Count -ne 1 -or $script:location.kind -ne 'folder') {return}
    $items[0].BeginEdit()
}
function New-RemoteFolder {
    if ($script:busy -or $script:location.kind -ne 'folder') {return}
    $names = @($list.Items | ForEach-Object {$_.Text})
    $name = '新增資料夾'; $n = 2
    while ($names -contains $name) {$name = '新增資料夾 ({0})' -f $n; $n++}
    $script:editAfter = $name
    $script:followUp = @{nav=$script:location}
    Begin-Operation @{action='mkdir'; device=$script:location.device; path=$script:location.path; name=$name}
}
function Copy-Into([string[]]$Paths,[string]$Folder) {
    $Paths = @($Paths | Where-Object {$_})
    if ($script:busy -or $script:location.kind -ne 'folder' -or $Paths.Count -eq 0) {return}
    $script:followUp = @{nav=$script:location}
    Begin-Operation @{action='copy'; device=$script:location.device; dest=$Folder; paths=$Paths}
}
function Add-FromDialog {
    if ($script:location.kind -ne 'folder') {return}
    # One dialog for both: select files, or open a folder and confirm the placeholder name to add the folder itself.
    $placeholder = '（加入目前資料夾）'
    $dialog = New-Object Windows.Forms.OpenFileDialog
    $dialog.Title = '加入檔案或資料夾'
    $dialog.Multiselect=$true; $dialog.ValidateNames=$false; $dialog.CheckFileExists=$false; $dialog.CheckPathExists=$true
    $dialog.FileName = $placeholder
    if ($dialog.ShowDialog() -eq 'OK') {
        $picked = foreach ($path in $dialog.FileNames) {
            if ([IO.Path]::GetFileName($path) -eq $placeholder) {[IO.Path]::GetDirectoryName($path)}
            elseif (Test-Path -LiteralPath $path) {$path}
        }
        Copy-Into @($picked) $script:location.path
    }
    $dialog.Dispose()
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
        if (-not $script:attached) {continue}
        $script:prompting = $true
        try {
            $choice = [Windows.Forms.MessageBox]::Show(('「{0}」已在電腦上修改。要將變更寫回隨身碟嗎？隨身碟上的原檔案會被取代。' -f $file.Name),'寫回隨身碟',[Windows.Forms.MessageBoxButtons]::YesNo,[Windows.Forms.MessageBoxIcon]::Question)
        } finally {$script:prompting = $false}
        if ($choice -eq [Windows.Forms.DialogResult]::Yes) {
            if ($script:location.kind -eq 'folder') {$script:followUp = @{nav=$script:location}}
            Begin-Operation @{action='copy'; device=$file.Device; dest=(Get-ParentPath $file.Remote); paths=@($file.Local); overwrite=$true; targetName=$file.Name}
        }
        return
    }
}

function Set-Busy([bool]$Value) {
    $script:busy = $Value
    if (-not $Value) {$bootProgress.Visible=$false; $transfer.Visible=$false; $list.Cursor=[Windows.Forms.Cursors]::Default}
    elseif ($script:pendingAction -in 'list','status','delete','rename','mkdir','eject','attach') {$list.Cursor=[Windows.Forms.Cursors]::AppStarting}
    Update-State
}
function Update-Guest($Guest) {
    $script:volumes = @()
    if ($Guest) {
        $script:ready = [bool]$Guest.ready
        $script:volumes = @($Guest.volumes | Where-Object {$_})
    }
}

# Drag and drop from Explorer: onto a folder copies into it, elsewhere into the current folder.
function Get-DropFolder($Sender,$E) {
    if ($Sender -eq $list) {
        $point = $list.PointToClient((New-Object Drawing.Point($E.X,$E.Y)))
        $item = $list.GetItemAt($point.X,$point.Y)
        if ($item -and $item.Tag.IsFolder) {return $item.Tag.Path}
    }
    return $script:location.path
}
$onDragOver = {
    param($sender,$e)
    if (-not $script:busy -and $script:location.kind -eq 'folder' -and $e.Data.GetDataPresent([Windows.Forms.DataFormats]::FileDrop)) {
        $e.Effect = [Windows.Forms.DragDropEffects]::Copy
    } else { $e.Effect = [Windows.Forms.DragDropEffects]::None }
}
$onDragDrop = {
    param($sender,$e)
    Copy-Into ([string[]]$e.Data.GetData([Windows.Forms.DataFormats]::FileDrop)) (Get-DropFolder $sender $e)
}
foreach ($target in @($form,$list,$emptyLabel)) {
    $target.AllowDrop = $true
    $target.Add_DragEnter($onDragOver); $target.Add_DragOver($onDragOver); $target.Add_DragDrop($onDragDrop)
}

$list.Add_ItemActivate({ Open-Selected })
$list.Add_SelectedIndexChanged({ Update-Counts })
$list.Add_KeyDown({
    param($sender,$e)
    if ($e.Alt -and $e.KeyCode -eq 'Left') {Go-Back; $e.Handled=$true; return}
    if ($e.Alt -and $e.KeyCode -eq 'Up') {Go-Up; $e.Handled=$true; return}
    if ($e.Control -and $e.KeyCode -eq 'A') {foreach ($item in $list.Items) {$item.Selected=$true}; $e.Handled=$true; return}
    switch ($e.KeyCode) {
        'Delete' {Remove-Selected}
        'F2' {Rename-Selected}
        'F5' {Refresh-View}
        'Back' {Go-Back}
    }
})
$list.Add_BeforeLabelEdit({ param($s,$e) if ($script:busy -or $script:location.kind -ne 'folder' -or $list.Items[$e.Item].Tag.IsLink) {$e.CancelEdit=$true} })
$list.Add_AfterLabelEdit({
    param($s,$e)
    $entry = $list.Items[$e.Item].Tag
    $name = if ($null -ne $e.Label) {$e.Label.Trim()} else {''}
    # The list keeps the old name until the rename succeeds and the folder is re-read.
    $e.CancelEdit = $true
    if (-not $name -or $name -eq $entry.Name -or $script:busy) {return}
    $script:followUp = @{nav=$script:location}
    Begin-Operation @{action='rename'; device=$entry.Device; path=$entry.Path; name=$name}
})
$viewPopup.Add_SelectedIndexChanged({
    if ($script:settingView) {return}
    switch ($viewPopup.SelectedIndex) { 0 {Set-Zoom ([int]$ui.iconStep)} 1 {Set-Zoom 1} default {Set-Zoom 0} }
})
$list.Add_CtrlWheel({ param($s,$e) if ($e.Delta -gt 0) {Set-Zoom ([int]$ui.zoom + 1)} elseif ($e.Delta -lt 0) {Set-Zoom ([int]$ui.zoom - 1)} })
$list.Add_HandleCreated({ Set-Zoom ([int]$ui.zoom) })
Set-Zoom ([int]$ui.zoom)

$menu = New-Object Windows.Forms.ContextMenuStrip
$menu.Renderer = New-Object UsbProxyUi.MenuRenderer
$menu.Font = $bodyFont
function Add-MenuItem([string]$Text,[scriptblock]$Handler,[string]$Keys='') {
    $item = New-Object Windows.Forms.ToolStripMenuItem($Text)
    if ($Keys) {$item.ShortcutKeyDisplayString = $Keys}
    $item.Add_Click($Handler)
    $null = $menu.Items.Add($item)
    return $item
}
$menuOpen = Add-MenuItem '開啟' { Open-Selected } 'Enter'
$menuSwitch = Add-MenuItem '切換到 Linux' { Open-Selected }
$menuSave = Add-MenuItem '存到電腦…' { Save-Selected }
$menuRename = Add-MenuItem '重新命名' { Rename-Selected } 'F2'
$menuDelete = Add-MenuItem '刪除' { Remove-Selected } 'Del'
$menuEject = Add-MenuItem '退出（交還 Windows）' { Eject-Drive }
$menuSeparator = New-Object Windows.Forms.ToolStripSeparator
$null = $menu.Items.Add($menuSeparator)
$menuNewFolder = Add-MenuItem '新增資料夾' { New-RemoteFolder }
$menuRefresh = Add-MenuItem '重新整理' { Refresh-View } 'F5'
$menu.Add_Opening({
    param($s,$e)
    $selected = @(Get-SelectedEntries); $idle = -not $script:busy
    $one = $selected.Count -eq 1
    $kind = if ($one) {$selected[0].Kind} else {''}
    $inFolder = $script:location.kind -eq 'folder'
    $menuOpen.Visible = $one -and $kind -ne 'device'
    $menuSwitch.Visible = $kind -eq 'device'
    $menuSave.Visible = $inFolder -and $selected.Count -gt 0
    $menuRename.Visible = $inFolder -and $selected.Count -gt 0
    $menuDelete.Visible = $inFolder -and $selected.Count -gt 0
    $menuEject.Visible = $kind -in 'volume','linux-device'
    $menuNewFolder.Visible = $inFolder
    $menuSeparator.Visible = $true
    $menuSave.Enabled = $idle -and $one -and $kind -eq 'file'
    $menuRename.Enabled = $idle -and $one -and $kind -ne 'link'
    foreach ($item in @($menuOpen,$menuSwitch,$menuDelete,$menuEject,$menuNewFolder,$menuRefresh)) {$item.Enabled = $idle}
})
$list.ContextMenuStrip = $menu; $emptyLabel.ContextMenuStrip = $menu

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
        if ($Request.action -eq 'start') {
            $script:bootStarted = Get-Date; $script:bootPhase = ''
            $script:bootEstimate = Get-BootEstimate
            $bootProgress.Value = 0; $bootProgress.Visible = $true
        }
        if ($Request.action -in 'copy','fetch') {Start-TransferView $Request}
        Set-Busy $true
        if ($Request.action -notin $quietActions) {
            Set-Message ($actionNames[$Request.action] + '…')
            Log-Line ('開始：' + $actionNames[$Request.action])
        }
    } catch {Log-Line $_.Exception.Message; $script:followUp = $null; Set-Busy $false}
}

function Start-TransferView($Request) {
    $script:samples.Clear()
    $crumb = $addressText.Text
    $transferTitle.Text = if ($Request.action -eq 'fetch') {
        if ($Request.saveTo) {('正在存到電腦：{0}' -f $Request.name)} else {('正在開啟：{0}' -f $Request.name)}
    } elseif ($Request.overwrite) {('正在寫回隨身碟：{0}' -f $Request.targetName)}
    else {('正在複製 {0} 個項目到 {1}' -f @($Request.paths).Count,$crumb)}
    $transferFile.Text = '準備中…'
    $transferStats.Text = '正在計算剩餘時間…'
    $transferBar.Value = 0
    $transfer.Visible = $true
}
function Update-TransferView {
    $file = $script:responsePath + '.progress'
    if (-not (Test-Path -LiteralPath $file)) {return}
    try {$p = Get-Content -Raw -Encoding UTF8 -LiteralPath $file | ConvertFrom-Json} catch {return}
    if (-not $p.total) {return}
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
    return 90
}
function Update-BootProgress {
    $progressFile = $script:responsePath + '.progress'
    if (Test-Path -LiteralPath $progressFile) {
        $phase = ''
        try {$phase = (Get-Content -Raw -Encoding UTF8 -LiteralPath $progressFile | ConvertFrom-Json).phase} catch { }
        if ($phase -and $phase -ne $script:bootPhase) {
            # The estimate restarts for a power-cycled boot or a one-time agent update.
            $script:bootPhase = $phase; $script:bootStarted = Get-Date
            if ($phase -eq 'retry') {Log-Line 'Linux 開機停滯，已自動重新啟動。'}
            if ($phase -eq 'updating') {$script:bootEstimate = [Math]::Max($script:bootEstimate,20); Log-Line '正在更新 Linux 內的元件。'}
            if ($phase -eq 'sealing') {$script:bootEstimate = [Math]::Max($script:bootEstimate,40); Log-Line '正在保存乾淨的 Linux 映像（只需一次），之後每次啟動都從這個狀態開始。'}
        }
    }
    $elapsed = ((Get-Date) - $script:bootStarted).TotalSeconds
    $estimate = $script:bootEstimate
    $prefix = switch ($script:bootPhase) { 'retry' {'重新啟動中'} 'updating' {'正在更新元件'} 'sealing' {'正在保存乾淨映像'} default {'正在啟動'} }
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
        # Devices settle for a moment after a plug or unplug before the list is re-read.
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
        if ($script:pendingAction -eq 'start') {Update-BootProgress}
        if ($script:pendingAction -in 'copy','fetch') {Update-TransferView}
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
                $wasAttached = $script:attached
                $script:attached = [bool]$data.vm.attachedUsb
                $script:attachedName = [string]$data.vm.attachedName
                $script:ready = $false
                Update-Guest $data.guest
                if ($data.message) {Log-Line ('Linux 尚未就緒或連線失敗：' + $data.message)}
                if ($script:switchTarget -and $script:ready -and -not $script:attached) {
                    $script:followUp = @{action='attach'; uuid=$script:switchTarget.uuid; name=$script:switchTarget.name}
                } elseif ($script:switchTarget) {
                    if (-not $script:ready) {Log-Line 'Linux 尚未就緒，無法切換隨身碟。'}
                    $script:switchTarget = $null
                }
                if ($wasAttached -and -not $script:attached) {Reset-ToComputer}
                elseif ($script:location.kind -eq 'pc') {Show-Computer}
                else {Update-State}
            }
            'start' {
                $script:vmState = 'running'; $script:ready = $true
                Log-Line ('Linux 啟動耗時 ' + (Format-Duration ([int]((Get-Date) - $script:bootStarted).TotalSeconds)))
                if ($data.agentUpdated) {Log-Line 'Linux 內的元件已更新。'}
                $script:followUp = @{action='status'}
            }
            'attach' {
                $script:attached = $true; $script:attachedName = [string]$request.name
                Update-Guest $data.guest
                $script:switchTarget = $null
                $script:history.Clear()
                if ($script:volumes.Count -eq 1) {
                    $script:location = @{kind='pc'}
                    $script:followUp = @{nav=@{kind='folder'; device=[string]$script:volumes[0].path; path=''}; push=$true}
                } else {Reset-ToComputer}
            }
            'list' {
                $nav = $script:pendingNav; $script:pendingNav = $null
                if ($nav -and $nav.push -and -not ($script:location.kind -eq 'folder' -and $script:location.device -eq $nav.target.device -and $script:location.path -eq [string]$data.path)) {
                    $script:history.Add($script:location)
                }
                $script:location = @{kind='folder'; device=[string]$request.device; path=[string]$data.path}
                Show-Folder $data ([string]$request.device)
            }
            'copy' {
                if ($request.overwrite) {Log-Line ('已寫回「{0}」，SHA-256 驗證通過。' -f $request.targetName)}
                else {Log-Line ('已複製並驗證 {0} 個檔案，共 {1}。' -f @($data.copied).Count,(Format-Size $data.bytes))}
            }
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
            'eject' {$script:attached = $false; $script:attachedName = ''; Update-Guest $null; Reset-ToComputer; $script:followUp = @{action='status'}}
            'shutdown' {
                $script:vmState = if ($data.off) {'poweroff'} else {'stopping'}
                $script:ready = $false
                if ($data.ejected) {$script:attached = $false; $script:attachedName = ''; Update-Guest $null; Reset-ToComputer; Log-Line '已自動退出隨身碟。'}
                $script:followUp = @{action='status'}
            }
        }
        if ($action -notin $quietActions) {
            Log-Line ('完成：' + $actionNames[$action])
            $done = switch ($action) {
                'copy' {if ($request.overwrite) {'已寫回隨身碟，SHA-256 驗證通過'} else {'複製完成，SHA-256 驗證通過'}}
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
        # A failed start still needs a status refresh; other follow-ups depend on success.
        $script:followUp = if ($action -eq 'start') {@{action='status'}} elseif ($action -in 'delete','rename','mkdir','copy') {@{nav=$script:location}} else {$null}
        if ($action -notin $quietActions) {[Windows.Forms.MessageBox]::Show(($actionNames[$action] + '失敗：' + "`n`n" + $reason),'USB Proxy',[Windows.Forms.MessageBoxButtons]::OK,[Windows.Forms.MessageBoxIcon]::Warning) | Out-Null}
    }
    finally {$script:process.Dispose(); $script:process = $null; Set-Busy $false}
    if ($script:closeWhenDone) {
        $script:closeWhenDone = $false
        if ($succeeded -and $action -eq 'shutdown') {$form.Close(); return}
    }
    if ($script:followUp) {
        $next = $script:followUp; $script:followUp = $null
        if ($next.nav) {
            if ($next.nav.kind -ne 'folder') {Show-Computer} elseif ($next.push) {Navigate $next.nav} else {Navigate $next.nav -NoHistory}
        } else {Begin-Operation $next}
    }
})

$form.Add_FormClosing({
    param($sender,$event)
    if ($script:busy) {$event.Cancel=$true; [Windows.Forms.MessageBox]::Show('工作進行中，請等待完成再關閉。','USB Proxy') | Out-Null; return}
    if ($script:attached) {
        $event.Cancel = $true
        $choice = [Windows.Forms.MessageBox]::Show('隨身碟仍在 Linux 中。要安全退出隨身碟、關閉 Linux 後再結束嗎？','USB Proxy',[Windows.Forms.MessageBoxButtons]::OKCancel,[Windows.Forms.MessageBoxIcon]::Question)
        if ($choice -eq [Windows.Forms.DialogResult]::OK) {$script:closeWhenDone = $true; Begin-Operation @{action='shutdown'}}
        return
    }
    # Remember the view and window size for next time.
    try {
        $bounds = if ($form.WindowState -eq 'Normal') {$form.ClientSize} else {$form.RestoreBounds.Size}
        $ui.width = [int]($bounds.Width / $scale); $ui.height = [int]($bounds.Height / $scale)
        $ui.maximized = $form.WindowState -eq 'Maximized'
        Write-Utf8 $uiFile ($ui | ConvertTo-Json)
    } catch { }
})
$form.Add_Shown({
    [UsbProxyUi.Native]::StyleWindow($form.Handle,[UsbProxyUi.Theme]::Background,[UsbProxyUi.Theme]::Label)
    if ($ui.maximized) {$form.WindowState = 'Maximized'}
})
if ([int]$ui.width -ge 760 -and [int]$ui.height -ge 540) {$form.ClientSize = New-Object Drawing.Size([int]$ui.width,[int]$ui.height)}
# Local copies of files opened in an earlier session; files still open elsewhere are skipped.
try { Get-ChildItem -LiteralPath (Join-Path $script:Runtime 'open') -Directory -ErrorAction Stop | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue } catch { }
Show-Computer
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
