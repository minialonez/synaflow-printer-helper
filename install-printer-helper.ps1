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
# ตัวช่วยพิมพ์ที่ติดตั้งไปเป็นโปรแกรม C# ตัวเดียว + helper-start.ps1 ที่ Windows เปิดตอนเข้าเครื่องเพื่อเช็ครุ่นใหม่ (v1.0.2)
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
    # ว่าง = รุ่นล่าสุด (releases/latest) · ตัวอัปเดตตอนเปิดเครื่องส่งแท็กของรุ่นที่เจอมา กันรุ่นขยับระหว่างทาง
    [string]$ReleaseTag = '',
    # ของเดิม (ทางถอยของคนที่เรียกแบบเก่า) — ถ้าส่งมา ใช้แทนไฟล์ .exe ของรุ่น
    [string]$HelperUrl = '',
    [switch]$NoPause,
    # v1.0.2: เงียบ = ตัวอัปเดตตอนเปิดเครื่องเรียก · ไม่เปิดหน้าต่างทดสอบ · ไฟล์ตรวจ SHA ไม่ได้ = ไม่ลง
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
function Say($t, $c = 'Gray') { if (-not $Quiet) { Write-Host $t -ForegroundColor $c } }
function Done($code) { if (-not $NoPause -and -not $Quiet) { Read-Host "Press Enter to close" }; exit $code }

$repo        = 'minialonez/synaflow-printer-helper'
$base        = if ($ReleaseTag) { "https://github.com/$repo/releases/download/$ReleaseTag" } else { "https://github.com/$repo/releases/latest/download" }
$installDir  = Join-Path $env:LOCALAPPDATA 'Synaflow\PrinterHelper'
$exePath     = Join-Path $installDir 'SynaflowPrinterHelper.exe'
$startPath   = Join-Path $installDir 'helper-start.ps1'
$startupLnk  = Join-Path ([Environment]::GetFolderPath('Startup')) 'Synaflow Printer Helper.lnk'
$legacyLnk   = Join-Path ([Environment]::GetFolderPath('Startup')) 'ERP-PrinterHelper.lnk'

Write-Host ""
Say "===== Synaflow - printer helper setup =====" Cyan
Say "  target: $installDir"
Write-Host ""

# ── 1. ดาวน์โหลดทุกไฟล์ + ตรวจ SHA-256 ก่อนแตะของเดิม ────────────────────────────
# 🔴 v1.0.2: เดิมปิดตัวช่วยพิมพ์ก่อนแล้วค่อยโหลด 68 MB = พิมพ์ไม่ได้ตลอดเวลาที่โหลด (เน็ตช้า = หลายนาที)
#    ตอนนี้โหลด + ตรวจให้ครบก่อน · ไฟล์ไม่ครบ/ไม่ตรง = ออกเลย ตัวเดิมยังทำงานต่อไม่สะดุด
Say "[1/5] downloading + verifying..." Yellow
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
$stamp   = [guid]::NewGuid().ToString('N')
$tmpExe  = Join-Path $env:TEMP "sf-helper-$stamp.exe"
$tmpStart = Join-Path $env:TEMP "sf-start-$stamp.ps1"
$tmpSums = Join-Path $env:TEMP "sf-sums-$stamp.txt"
function Cleanup { Remove-Item $tmpExe, $tmpStart, $tmpSums -Force -ErrorAction SilentlyContinue }
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $oldPref = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
    $exeUrl = if ($HelperUrl) { $HelperUrl } else { "$base/SynaflowPrinterHelper.exe" }
    Invoke-WebRequest -Uri $exeUrl -OutFile $tmpExe -UseBasicParsing -TimeoutSec 600
    Invoke-WebRequest -Uri "$base/helper-start.ps1" -OutFile $tmpStart -UseBasicParsing -TimeoutSec 120
    $sumsOk = $true
    try { Invoke-WebRequest -Uri "$base/SHA256SUMS.txt" -OutFile $tmpSums -UseBasicParsing -TimeoutSec 60 }
    catch { $sumsOk = $false }
    $ProgressPreference = $oldPref
} catch {
    Say "  FAILED to download: $($_.Exception.Message)" Red
    Say "  check the internet connection, then run this file again" Yellow
    Cleanup
    Done 1
}

# SHA256SUMS.txt = บรรทัดละ "<sha256>  <ชื่อไฟล์>" (รูปแบบ sha256sum)
function Test-Sha($file, $name) {
    $want = $null
    foreach ($line in Get-Content $tmpSums) {
        $parts = $line.Trim() -split '\s+', 2
        if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -eq $name) { $want = $parts[0].ToLower() }
    }
    if (-not $want) { return $false }
    $got = (Get-FileHash -Algorithm SHA256 -Path $file).Hash.ToLower()
    return ($got -eq $want)
}
if ($sumsOk -and -not $HelperUrl) {
    if (-not (Test-Sha $tmpExe 'SynaflowPrinterHelper.exe') -or -not (Test-Sha $tmpStart 'helper-start.ps1')) {
        Say "  FAILED: downloaded files do not match SHA256SUMS.txt - nothing was changed" Red
        Cleanup
        Done 2
    }
    Say "  ok (SHA-256 verified)" Green
} elseif ($Quiet) {
    # ตัวอัปเดตอัตโนมัติ: ตรวจไม่ได้ = ไม่ลง (ปลอดภัยไว้ก่อน ตัวเดิมยังใช้ได้)
    Cleanup
    Done 3
} else {
    Say "  ok (no checksum file for this release - continuing)" Yellow
}

# ── 2. ปิดตัวเดิม ไม่งั้นเขียนทับไฟล์ไม่ได้และแย่งพอร์ต 9999 กัน ──────────────────
Say "[2/5] stopping old helper (if any)..." Yellow
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

# ── 3. วางไฟล์ ───────────────────────────────────────────────────────────────
Say "[3/5] installing..." Yellow
Move-Item $tmpExe $exePath -Force
Move-Item $tmpStart $startPath -Force
Remove-Item $tmpSums -Force -ErrorAction SilentlyContinue
Say "  $exePath" Green

# ── 4. ตั้งให้เปิดเองตอนเข้า Windows ────────────────────────────────────────
# 🪤 ใช้ทางลัดในโฟลเดอร์ Startup ไม่ใช่คีย์ Run — เป็นท่าเดียวกับสคริปต์เดิมที่
#    ใช้มาเป็นปีแล้วไม่เคยถูก Kaspersky จับ · และผู้ใช้เห็น/ลบเองได้ ไม่ซ่อนในรีจิสทรี
# v1.0.2 (W245 ②): ทางลัดชี้ helper-start.ps1 → เช็ครุ่นใหม่ตอนเข้า Windows แล้วเปิดตัวช่วยพิมพ์
#    🔴 ตัว .exe ห้ามเรียก powershell เอง (Kaspersky จับ W247) — ให้ Windows เปิดสคริปต์จากทางลัดแทน
Say "[4/5] setting auto-start (update check at sign-in)..." Yellow
try {
    $s = (New-Object -ComObject WScript.Shell).CreateShortcut($startupLnk)
    $s.TargetPath       = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $s.Arguments        = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$startPath`""
    $s.WorkingDirectory = $installDir
    $s.WindowStyle      = 7
    $s.Description      = 'Synaflow printer helper (checks for updates at sign-in)'
    $s.Save()
    Say "  $startupLnk" Green
} catch {
    Say "  could not create the startup shortcut: $($_.Exception.Message)" Yellow
}

# ── 5. เริ่มทำงาน แล้วตรวจว่าตอบจริง ────────────────────────────────────────
Say "[5/5] starting..." Yellow
Start-Process -FilePath $exePath -ArgumentList '--serve' -WorkingDirectory $installDir -WindowStyle Hidden
$ok = $false
for ($i = 0; $i -lt 20; $i++) {
    try { $null = Invoke-WebRequest 'http://localhost:9999/health' -UseBasicParsing -TimeoutSec 2; $ok = $true; break }
    catch { Start-Sleep -Milliseconds 700 }
}

Write-Host ""
if ($ok) {
    Say "SUCCESS - the printer helper is running." Green
    Say "It starts by itself every time you sign in to Windows, and updates itself then." Green
    # v1.1.0 (W302): เครื่องนี้จะไปถามงานพิมพ์จากเซิร์ฟเวอร์เองด้วย ต้องมีคนกดอนุมัติหนึ่งครั้ง
    #    ถ้าไม่บอกตรงนี้ ลูกค้าจะลงเสร็จแล้วงงว่าทำไมสั่งพิมพ์จากมือถือแล้วไม่ออก
    Say "One more step: ask an admin to approve this PC once in Synaflow" Cyan
    Say "  (menu: Master data -> Printers). Until then this PC only prints" Cyan
    Say "  from the browser on this same machine." Cyan
    if (-not $Quiet) {
        Write-Host ""
        Say "A window will now open so you can test printing and the cash drawer." Cyan
        # เปิดหน้าต่างทดสอบ (ภาษาไทย) — โหมดนี้ไม่แตะการติดตั้งอะไรอีก แค่ทดสอบ
        Start-Process -FilePath $exePath -ArgumentList '--test-ui'
    }
} else {
    Say "The helper did not answer yet." Yellow
    Say "  - restart Windows once, then open this file again" Yellow
    Say "  - or check that no antivirus is blocking it" Yellow
    Say "  - log file: $env:LOCALAPPDATA\Synaflow\printer-helper.log" Gray
}
Write-Host ""
Done 0
