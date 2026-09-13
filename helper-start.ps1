# helper-start.ps1
# ============================================================================
# ตัวเปิด "ตัวช่วยพิมพ์" ตอนเข้า Windows + อัปเดตตัวเองถ้ามีรุ่นใหม่ (v1.0.2 · W245 ② · 14 ก.ย. 2569)
#
# Toppy เคาะ: "อัปเดทตอนเปิดเครื่องสะดวกดี"
#   ⇒ ทางลัดในโฟลเดอร์ Startup ชี้มาที่ไฟล์นี้ (ไม่ชี้ตัว .exe ตรงแล้ว)
#   ⇒ ทุกครั้งที่เข้า Windows: ถามรุ่นล่าสุดจาก GitHub → ใหม่กว่าที่ลงไว้ = รันตัวติดตั้งตัวเดิมแบบเงียบ
#      → เท่ากับ ไม่ใหม่กว่า / เน็ตไม่มี / อะไรพังก็ตาม = เปิดตัวช่วยพิมพ์ตัวเดิมเสมอ (ห้ามทำให้พิมพ์ไม่ได้)
#
# 🔴 ทำไมไม่ให้ตัว .exe อัปเดตตัวเอง (W247 · 12 ก.ย. 2569)
#    Kaspersky จับ .exe ที่ "เรียก powershell.exe รันสคริปต์" เป็น Trojan จากพฤติกรรม
#    ⇒ .exe ของเราไม่เรียก powershell เด็ดขาด · ไฟล์นี้ถูก Windows เปิดจากทางลัด Startup
#      ท่าเดียวกับตัวช่วยพิมพ์รุ่นสคริปต์ที่ใช้มาเป็นปีไม่เคยถูกจับ
#
# 🪤 อัปเดตได้เฉพาะตอนเข้า Windows = ก่อนเริ่มขาย · ไม่มีทางอัปเดตกลางการขาย
# 🪤 ตัวติดตั้งตรวจ SHA-256 ทุกไฟล์ก่อนวาง — ไฟล์ไม่ตรง = ไม่ลง แล้วใช้ตัวเดิมต่อ
#
# NOTE: live strings = ASCII only (PS 5.1 reads this file as ANSI)
# ============================================================================
param(
    # ทดสอบเท่านั้น: แกล้งว่ารุ่นที่ลงไว้เก่ากว่า เพื่อบังคับให้ลองอัปเดต
    [string]$AssumeVersion = ''
)

$ErrorActionPreference = 'Continue'
$repo       = 'minialonez/synaflow-printer-helper'
$installDir = Join-Path $env:LOCALAPPDATA 'Synaflow\PrinterHelper'
$exePath    = Join-Path $installDir 'SynaflowPrinterHelper.exe'
$logDir     = Join-Path $env:LOCALAPPDATA 'Synaflow'
$logPath    = Join-Path $logDir 'printer-helper-update.log'

function Log($t) {
    try {
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
        Add-Content -Path $logPath -Value ("[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $t)
    } catch {}
}

function Start-HelperIfNotRunning {
    if (-not (Test-Path $exePath)) { Log "helper exe missing: $exePath"; return }
    if (Get-Process 'SynaflowPrinterHelper' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $exePath }) { return }
    Start-Process -FilePath $exePath -ArgumentList '--serve' -WorkingDirectory $installDir -WindowStyle Hidden
}

# รุ่นที่ลงไว้ — ProductVersion ของ .NET เป็นแบบ "1.0.2+<commit>" ต้องตัดส่วนหลัง + ทิ้ง
function Get-InstalledVersion {
    if ($AssumeVersion) { return [version]$AssumeVersion }
    if (-not (Test-Path $exePath)) { return [version]'0.0.0' }
    $raw = (Get-Item $exePath).VersionInfo.ProductVersion
    if (-not $raw) { $raw = (Get-Item $exePath).VersionInfo.FileVersion }
    $clean = ($raw -split '\+')[0].Trim()
    try { return [version]$clean } catch { return [version]'0.0.0' }
}

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue'

    $installed = Get-InstalledVersion
    $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" `
        -Headers @{ 'User-Agent' = 'synaflow-printer-helper'; 'Accept' = 'application/vnd.github+json' } `
        -UseBasicParsing -TimeoutSec 20
    $tag = [string]$rel.tag_name
    $latest = [version]($tag.TrimStart('v', 'V'))

    if ($latest -gt $installed) {
        Log "update available: installed $installed -> latest $latest"
        $tmp = Join-Path $env:TEMP ("sf-install-" + [guid]::NewGuid().ToString('N') + ".ps1")
        # ตัวติดตั้งของ "รุ่นที่เจอ" ตรง ๆ (ไม่ใช่ latest) กันรุ่นขยับระหว่างทาง
        Invoke-WebRequest -Uri "https://github.com/$repo/releases/download/$tag/install-printer-helper.ps1" `
            -OutFile $tmp -UseBasicParsing -TimeoutSec 120
        & $tmp -NoPause -Quiet -ReleaseTag $tag
        $code = $LASTEXITCODE
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        Log "installer finished (exit $code)"
    }
} catch {
    Log ("update check skipped: " + $_.Exception.Message)
}

# ทุกทาง (อัปเดตสำเร็จ / ล้ม / ไม่มีเน็ต / ไม่มีรุ่นใหม่) จบที่ตัวช่วยพิมพ์ต้องทำงาน
Start-HelperIfNotRunning
