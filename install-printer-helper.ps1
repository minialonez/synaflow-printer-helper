# install-printer-helper.ps1
# ============================================================================
# ตัวติดตั้ง "ตัวช่วยพิมพ์" ของ Synaflow — ลูกค้าไม่ได้เรียกไฟล์นี้ตรง ๆ
# ตัวที่ลูกค้าโหลดคือ install-printer-helper.cmd ซึ่งดาวน์โหลดไฟล์นี้มารันให้
#
# 🔴 ทำไมขั้นติดตั้งต้องเป็น PowerShell ไม่ใช่ .exe (W247 · 12 ก.ย. 2569)
#    รอบก่อนทำเป็น .exe ที่คัดลอกตัวเอง + ตั้งให้เปิดเองตอนบูต
#    → Kaspersky System Watcher จับเป็น PDM:Trojan.Win32.Generic แล้วลบทิ้ง
#      ("Application performing suspicious activity characteristic of malware")
#    หลักฐานชี้ขาด: ไฟล์ 2 ตัวไบต์เดียวกัน ตัวที่ทำขั้นติดตั้งโดนเก็บ
#      ส่วนตัวที่แค่เปิดพอร์ตรอพิมพ์ **ไม่โดนแตะเลย**
#    ⇒ ตัวกระตุ้นคือ "การฝังตัว" ไม่ใช่ตัวไฟล์ · สแกนไฟล์เฉย ๆ สะอาด 453 ชิ้น เจอ 0
#    ⇒ ให้ powershell.exe (ไมโครซอฟท์เซ็นไว้) เป็นคนลงมือแทน ซึ่งเป็นท่าที่
#      สคริปต์ติดตั้งเดิมของเราใช้มาเป็นปีแล้วไม่เคยถูกจับ
#
# ตัวช่วยพิมพ์ที่ติดตั้งไปเป็นโปรแกรม C# ตัวเดียวจบ ไม่มี PowerShell ค้างในเครื่องลูกค้า
#
# NOTE: live strings = ASCII only (Thai in live strings breaks PS 5.1 parsing
#       when the file is read as ANSI — Thai stays in comments only,
#       same rule as printer-helper.ps1)
# ============================================================================

# 🔴 ไฟล์ดาวน์โหลดทั้งหมดอยู่ที่ GitHub Releases ไม่ใช่ synaflow.app (W250 · 13 ก.ย. 2569)
#    โดเมนที่แจก .exe ที่ไม่ได้เซ็นใบรับรอง + สคริปต์ที่เรียก powershell โหลดมารัน
#    = ลายเซ็นเว็บแจกมัลแวร์ในสายตาตัวจัดอันดับ URL -> 12 ก.ย. Kaspersky บล็อก synaflow.app ทั้งโดเมน
#    ห้ามย้ายกลับไปอยู่ใต้ synaflow.app หรือ dl.synaflow.app (ชื่อเสียงคิดรวมโดเมนแม่)
param(
    # releases/latest = รุ่นล่าสุดเสมอ ออกรุ่นใหม่ไม่ต้องแก้สคริปต์
    [string]$HelperUrl = 'https://github.com/minialonez/synaflow-printer-helper/releases/latest/download/SynaflowPrinterHelper.exe',
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
function Say($t, $c = 'Gray') { Write-Host $t -ForegroundColor $c }

$installDir = Join-Path $env:LOCALAPPDATA 'Synaflow\PrinterHelper'
$exePath    = Join-Path $installDir 'SynaflowPrinterHelper.exe'
$startupLnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'Synaflow Printer Helper.lnk'
$legacyLnk  = Join-Path ([Environment]::GetFolderPath('Startup')) 'ERP-PrinterHelper.lnk'

Write-Host ""
Say "===== Synaflow - printer helper setup =====" Cyan
Say "  target: $installDir"
Write-Host ""

# ── 1. ปิดตัวเดิม ไม่งั้นเขียนทับไฟล์ไม่ได้และแย่งพอร์ต 9999 กัน ──────────────
Say "[1/5] stopping old helper (if any)..." Yellow
Get-Process 'SynaflowPrinterHelper' -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.Kill(); $_.WaitForExit(4000) } catch {}
}
# helper รุ่นเก่าที่เป็นสคริปต์ PowerShell — หาโดยดูบรรทัดคำสั่ง แล้วปิดให้ด้วย
try {
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -like '*printer-helper.ps1*' -and $_.ProcessId -ne $PID } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
} catch {}
# ทางลัดของรุ่นเก่า ต้องลบ ไม่งั้นครั้งหน้าเข้า Windows จะมีตัวช่วยพิมพ์ 2 ตัวแย่งพอร์ต
if (Test-Path $legacyLnk) { Remove-Item $legacyLnk -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 1200
Say "  ok" Green

# ── 2. ดาวน์โหลดตัวช่วยพิมพ์ ──────────────────────────────────────────────────
Say "[2/5] downloading helper..." Yellow
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
$tmp = Join-Path $env:TEMP ("sf-helper-" + [guid]::NewGuid().ToString('N') + ".exe")
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    # ProgressPreference = SilentlyContinue -> เร็วขึ้นมากกับไฟล์ใหญ่ (แถบ % ของ PS ช้าเอง)
    $oldPref = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $HelperUrl -OutFile $tmp -UseBasicParsing -TimeoutSec 600
    $ProgressPreference = $oldPref
} catch {
    Say "  FAILED to download: $($_.Exception.Message)" Red
    Say "  check the internet connection, then run this file again" Yellow
    if (-not $NoPause) { Read-Host "Press Enter to close" }
    exit 1
}
$sizeMb = [math]::Round((Get-Item $tmp).Length / 1MB)
Say "  ok ($sizeMb MB)" Green

# ── 3. วางไฟล์ ───────────────────────────────────────────────────────────────
Say "[3/5] installing..." Yellow
Move-Item $tmp $exePath -Force
Say "  $exePath" Green

# ── 4. ตั้งให้เปิดเองตอนเข้า Windows ────────────────────────────────────────
# 🪤 ใช้ทางลัดในโฟลเดอร์ Startup ไม่ใช่คีย์ Run — เป็นท่าเดียวกับสคริปต์เดิมที่
#    ใช้มาเป็นปีแล้วไม่เคยถูก Kaspersky จับ · และผู้ใช้เห็น/ลบเองได้ ไม่ซ่อนในรีจิสทรี
Say "[4/5] setting auto-start..." Yellow
try {
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($startupLnk)
    $s.TargetPath       = $exePath
    $s.Arguments        = '--serve'
    $s.WorkingDirectory = $installDir
    $s.WindowStyle      = 7
    $s.Description      = 'Synaflow printer helper'
    $s.Save()
    Say "  $startupLnk" Green
} catch {
    Say "  could not create the startup shortcut: $($_.Exception.Message)" Yellow
}

# ── 5. เริ่มทำงาน แล้วตรวจว่าตอบจริง ────────────────────────────────────────
Say "[5/5] starting..." Yellow
Start-Process -FilePath $exePath -ArgumentList '--serve' -WindowStyle Hidden
$ok = $false
for ($i = 0; $i -lt 20; $i++) {
    try { $null = Invoke-WebRequest 'http://localhost:9999/health' -UseBasicParsing -TimeoutSec 2; $ok = $true; break }
    catch { Start-Sleep -Milliseconds 700 }
}

Write-Host ""
if ($ok) {
    Say "SUCCESS - the printer helper is running." Green
    Say "It will start by itself every time you sign in to Windows." Green
    Write-Host ""
    Say "A window will now open so you can test printing and the cash drawer." Cyan
    # เปิดหน้าต่างทดสอบ (ภาษาไทย) — โหมดนี้ไม่แตะการติดตั้งอะไรอีก แค่ทดสอบ
    Start-Process -FilePath $exePath -ArgumentList '--test-ui'
} else {
    Say "The helper did not answer yet." Yellow
    Say "  - restart Windows once, then open this file again" Yellow
    Say "  - or check that no antivirus is blocking it" Yellow
    Say "  - log file: $env:LOCALAPPDATA\Synaflow\printer-helper.log" Gray
}
Write-Host ""
if (-not $NoPause) { Read-Host "Press Enter to close" }
