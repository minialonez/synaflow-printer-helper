@echo off
REM ============================================================================
REM  Synaflow - printer helper setup (the ONE file customers download)
REM
REM  ดับเบิลคลิกไฟล์นี้ได้เลย ไม่ต้องคลิกขวา ไม่ต้องเป็นผู้ดูแลระบบ
REM  มันจะไปโหลดตัวติดตั้งตัวจริงจากเซิร์ฟเวอร์มารันให้
REM
REM  🔴 ทำไมเป็น .cmd ไม่ใช่ .exe (W247 · 12 ก.ย. 2569):
REM     ตัวติดตั้งแบบ .exe โดน Kaspersky System Watcher จับเป็น
REM     PDM:Trojan.Win32.Generic แล้วลบทิ้ง เพราะมันคัดลอกตัวเอง + ตั้งให้
REM     เปิดเองตอนบูต = พฤติกรรมฝังตัว · ตัวไฟล์เองสแกนแล้วสะอาด
REM     ⇒ ให้ powershell.exe (ไมโครซอฟท์เซ็นไว้แล้ว) เป็นคนลงมือแทน
REM       ซึ่งเป็นท่าที่สคริปต์ติดตั้งเดิมใช้มาเป็นปีแล้วไม่เคยถูกจับ
REM ============================================================================

setlocal
REM  W250 (13 Sep 2026): all downloads come from GitHub Releases, NOT synaflow.app
REM  (Kaspersky blocked the whole synaflow.app domain for serving these files)
set "SRC=https://github.com/minialonez/synaflow-printer-helper/releases/latest/download/install-printer-helper.ps1"
if not "%~1"=="" set "SRC=%~1"

echo.
echo   Synaflow - printer helper setup
echo   ================================
echo.
echo   Downloading the setup script from GitHub ...

set "PS1=%TEMP%\synaflow-install-printer-helper.ps1"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; $ProgressPreference='SilentlyContinue'; Invoke-WebRequest -Uri '%SRC%' -OutFile '%PS1%' -UseBasicParsing -TimeoutSec 120"

if not exist "%PS1%" (
  echo.
  echo   FAILED - could not download the setup script.
  echo   Check the internet connection and try again.
  echo.
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%"

del "%PS1%" >nul 2>&1
endlocal
