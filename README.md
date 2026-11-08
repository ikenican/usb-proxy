# USB Proxy

在 Windows 上，透過隔離的 Linux 虛擬機管理 USB 隨身碟：Windows 不直接掛載隨身碟，所有讀寫都在 Linux 內完成，並以 SHA-256 驗證。介面操作接近 Windows「本機」與檔案總管。

![app icon](linxupenguin.png)

## 功能

- **本機檢視**：插入的隨身碟顯示為磁碟機圖示，分成「在 Windows 中」與「在 Linux 中」；插拔自動更新。
- **一鍵切換到 Linux**：按兩下隨身碟並確認，Linux 未啟動時會自動啟動（顯示進度與預估剩餘時間），接管後直接開啟。
- **檔案總管式瀏覽**：Windows 原生檔案類型圖示；大圖示／清單／詳細資料檢視，Ctrl＋滑鼠滾輪縮放；欄位排序；位址列、返回、上一層；右鍵選單。
- **複製**：把檔案或資料夾拖放進視窗（可拖到某個資料夾上），或按「加入…」。顯示目前檔案、階段（傳送到 Linux／寫入隨身碟／驗證）、進度、剩餘時間與預計完成時間。每個檔案都經 SHA-256 讀回驗證，不會覆寫同名檔案。
- **開啟與編輯**：按兩下檔案即以 Windows 預設程式開啟驗證過的副本；存檔後會詢問是否寫回隨身碟。
- **管理**：存到電腦、重新命名（F2，就地編輯）、刪除（Delete，可多選）、新增資料夾、重新整理（F5）。
- **安全退出**：「退出」把隨身碟交還 Windows；關閉 Linux 時若隨身碟仍在 Linux 中會先自動退出。
- 支援 FAT32、exFAT、NTFS、ext4。不提供格式化、分割或整碟寫入。

## 安裝與使用（測試機）

1. 從 [Releases](../../releases) 下載 `USBProxy-*-full.zip`，解壓縮到本機硬碟。
2. 執行 `Setup-USBProxy.cmd` 並核准系統管理員提示（安裝官方簽章的 Oracle VirtualBox，僅含主程式與 USB 支援，並建立 Linux 虛擬機）。第一次啟動 Linux 需要網路下載套件。
3. 執行 `Start-USBProxy.cmd`。詳細操作見 [使用說明.txt](使用說明.txt)。

測試機不需要 Visual Studio、WDK 或 Python。

## 架構

```
Windows                                     Linux VM (Debian 13, VirtualBox, NAT)
src/UsbProxy.ps1  (WinForms UI)             /usr/local/sbin/usbproxy-agent (guest/usbproxy-agent.py)
  └─ scripts/Worker.ps1 (每個操作一個行程)      ├─ 只掛載 USB 匯流排上的分割區於 /mnt/usbproxy
       ├─ VBoxManage：啟動/關機/USB 接管          ├─ 列出、刪除、重新命名、新增資料夾
       └─ ssh/scp (127.0.0.1)  ───────────────►  ├─ 複製：tar 串流 → 寫入 → 讀回 SHA-256 → 原子更名
                                                  └─ 串流讀取：檔案位元組走 stdout，結果走 stderr
```

- 介面與自繪元件：`src/UsbProxy.ps1`、`src/AppleUi.cs`（啟動時由 PowerShell 編譯）。
- 背景操作：`scripts/Worker.ps1`，以 JSON 請求／回應檔與介面溝通，進度寫入 `*.progress`。
- 安裝：`scripts/Install-Host.ps1`、`scripts/Prepare-VM.ps1`、`scripts/New-GuestSeed.ps1`（cloud-init）。
- 打包：`scripts/Build-Package.ps1` 產生 `dist/USBProxy-<時間>`。

開發細節、已知限制與後續工作見 [AI.md](AI.md)。

## 安全與變更範圍

- 不修改 USBSTOR、UASPStor、Secure Boot、記憶體完整性、BIOS、永久執行原則或 Windows 選用功能。
- VM 只有 NAT 網路，SSH 僅綁定 127.0.0.1，使用專用金鑰（存放於 `runtime/`，不會進版控）。
- USB 只在使用者確認後逐一接管，不使用萬用篩選器。
- 安裝前會在 `backups/` 保留相關登錄與驅動清單快照（非完整系統映像）。

## 授權

尚未指定授權條款。Release 內附的 Oracle VirtualBox 安裝程式與 Debian 雲端映像各自依其原始授權散布。
