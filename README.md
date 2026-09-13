# Synaflow Printer Helper

A small Windows utility that lets the **Synaflow POS** web app (https://synaflow.app)
print receipts silently and open the cash drawer — things a browser cannot do on its own.

It is installed on the shop's own counter PC by the shop owner. It is not a general-purpose
program and does nothing unless the Synaflow web app asks it to.

## What it does

- Runs in the background and listens on **`localhost:9999` only** — it is not reachable from
  the network or the internet.
- Accepts requests **only from `https://synaflow.app` and its direct subdomains**
  (`https://<name>.synaflow.app`, e.g. `https://jknfc.synaflow.app` — added in v1.0.1).
  Strict CORS allowlist: https only, default port, one lowercase label; any other origin
  (including look-alikes such as `evilsynaflow.app`) is rejected with HTTP 403 before anything happens.
- Sends print jobs to a printer the shop has already installed in Windows, and sends the
  standard ESC/POS "open drawer" pulse (`ESC p 0 50 250`) to the receipt printer.
- To print **PDF** documents it uses **SumatraPDF**, the open-source PDF viewer. If SumatraPDF
  is not already on the PC, the helper downloads the official 64-bit build
  (`https://www.sumatrapdfreader.org/dl/rel/3.5.2/SumatraPDF-3.5.2-64.exe`) **once** into
  `%LOCALAPPDATA%\SumatraPDF\` and runs it to print.
  *Known gap: that download is not yet verified against a SHA-256 checksum.*
- To print **price labels** it runs the Chrome or Edge already installed on the PC, in headless
  mode, to turn the label page into a PDF.

## What it does **not** do

- No telemetry and no data collection — nothing about the shop or its sales leaves the PC.
- The only outbound download is SumatraPDF, described above.
- No self-update.
- Requires no administrator rights.

## Install (for shop owners)

In Synaflow go to **ข้อมูลหลัก → เครื่องพิมพ์** and press the download button,
then double-click the downloaded `Synaflow-Printer-Helper-Setup.cmd`.

It downloads the helper from this repository's latest release, copies it to
`%LOCALAPPDATA%\Synaflow\PrinterHelper`, adds a shortcut to the Windows **Startup**
folder (visible and removable by the user — no hidden registry entries), starts it,
and opens a test window.

**Uninstall:** delete the shortcut `Synaflow Printer Helper` from the Startup folder
(`Win+R` → `shell:startup`) and the folder `%LOCALAPPDATA%\Synaflow\PrinterHelper`.

## Files in each release

| File | Purpose |
|---|---|
| `Synaflow-Printer-Helper-Setup.cmd` | What the shop owner double-clicks |
| `install-printer-helper.ps1` | The actual install steps, downloaded and run by the `.cmd` |
| `SynaflowPrinterHelper.exe` | The helper itself (.NET 8, self-contained single file) |

The `.exe` is **not code-signed yet**. Every release lists the SHA-256 of each file so it
can be verified.

## Build from source

```
dotnet publish src/PrinterHelperSetup.csproj -c Release
```

Output: `src/bin/Release/net8.0-windows/win-x64/publish/SynaflowPrinterHelper.exe`

## Why the downloads live here and not on synaflow.app

Serving an unsigned executable plus a download-and-run script from the same domain as the
web app caused an antivirus URL-reputation database to block the entire app domain.
Keeping the downloads on GitHub means that can never take the POS itself offline again.

---

**ภาษาไทย:** โปรแกรมเล็ก ๆ ที่ร้านลงไว้บนเครื่องคอมหน้าร้าน เพื่อให้หน้าเว็บ Synaflow
พิมพ์ใบเสร็จแบบไม่ต้องกดยืนยันและสั่งเปิดลิ้นชักเก็บเงินได้ ฟังเฉพาะในเครื่องตัวเอง
(`localhost:9999`) รับคำสั่งจาก `synaflow.app` และโดเมนย่อยของ synaflow.app เท่านั้น (เช่น `jknfc.synaflow.app` · ตั้งแต่ v1.0.1) ไม่ส่งข้อมูลออกไปไหน
ติดตั้งจากเมนู **ข้อมูลหลัก → เครื่องพิมพ์** ในระบบ
