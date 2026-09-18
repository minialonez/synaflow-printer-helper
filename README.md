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
- **Since v1.1.0 it can also fetch print jobs from the shop's own Synaflow server** (see below),
  so printing keeps working from a phone, a tablet, or a browser that blocks `localhost` calls.

## Fetching print jobs from the server (new in v1.1.0)

Browsers are closing the door on https pages calling `http://localhost` (Local Network Access,
Edge 143+/Chrome 141+). Rather than asking every shop to grant a per-machine permission, the
helper can ask the server for work instead:

1. On first start it registers itself: `POST https://synaflow.app/api/print-agent/register`
   with the **PC name, the helper version and the list of printers installed on that PC**.
   The server replies with a secret shown only once; the helper stores it in
   `%LOCALAPPDATA%\Synaflow\agent.json` (that file is the machine's key — treat it as a password,
   it is never written to the log).
2. Until an admin approves the PC in **ข้อมูลหลัก → เครื่องพิมพ์** the server answers
   `403 agent_pending` and the helper just keeps waiting (slowly, one log line).
3. Once approved it holds a long-poll (`GET /api/print-agent/poll?wait=25`), downloads the job's
   bytes, prints them through exactly the same code paths as the `localhost` route above, and
   posts the outcome back. Payloads larger than **20 MB** are refused without downloading.
4. The old `localhost:9999` route stays and is still preferred when the browser allows it —
   it is faster. Both routes run at the same time.

**What leaves the PC:** the PC name, the helper version, the names of the printers installed on
it, and the success/failure of each print job. Nothing else — no sales data, no files, no scanning
of the machine. All of it goes to the shop's own Synaflow server and nowhere else.
Point the helper somewhere else with `--server=https://…` (or the `SYNAFLOW_SERVER` environment
variable), or switch the whole thing off with `--no-cloud` and keep using `localhost:9999` only.

## What it does **not** do

- No telemetry and no data collection — the only thing it ever sends is what is listed above,
  and only to the shop's own Synaflow server.
- Outbound connections: the shop's Synaflow server (print jobs) and the SumatraPDF download
  described above. Nothing else.
- No self-update (the Startup shortcut runs `helper-start.ps1`, which checks GitHub for a newer
  release when you sign in to Windows — you can delete the shortcut to stop that).
- Requires no administrator rights.

## Install (for shop owners)

In Synaflow go to **ข้อมูลหลัก → เครื่องพิมพ์** and press the download button,
then double-click the downloaded `Synaflow-Printer-Helper-Setup.cmd`.

It downloads the helper from this repository's latest release, copies it to
`%LOCALAPPDATA%\Synaflow\PrinterHelper`, adds a shortcut to the Windows **Startup**
folder (visible and removable by the user — no hidden registry entries), starts it,
and opens a test window.

After installing, an admin has to approve the PC once in Synaflow
(**ข้อมูลหลัก → เครื่องพิมพ์**) before the server can send it print jobs. The test window
tells you which of the two states the PC is in (⏳ waiting for approval / ✅ connected).

**Uninstall:** delete the shortcut `Synaflow Printer Helper` from the Startup folder
(`Win+R` → `shell:startup`) and the folder `%LOCALAPPDATA%\Synaflow\PrinterHelper`.
Also delete `%LOCALAPPDATA%\Synaflow\agent.json` (the machine key) and press ปฏิเสธ/ถอนสิทธิ์
for that PC in Synaflow.

## Files it writes

| Path | What |
|---|---|
| `%LOCALAPPDATA%\Synaflow\agent.json` | This PC's key for the server — **secret**, never logged |
| `%LOCALAPPDATA%\Synaflow\printer-helper.log` | Activity log (rotates at 2 MB to `printer-helper.1.log`) |
| `%LOCALAPPDATA%\SumatraPDF\SumatraPDF.exe` | Downloaded once if SumatraPDF is not already installed |

## Command line

| Argument | Effect |
|---|---|
| `--serve` | Background mode: `localhost:9999` **and** fetching jobs from the server |
| *(none)* / `--test-ui` | The Thai status/test window |
| `--server=https://…` | Which server to fetch jobs from (default `https://synaflow.app`; env `SYNAFLOW_SERVER` also works) |
| `--no-cloud` | Do not contact any server — behave exactly like v1.0.x |

## Files in each release

| File | Purpose |
|---|---|
| `Synaflow-Printer-Helper-Setup.cmd` | What the shop owner double-clicks |
| `install-printer-helper.ps1` | The actual install steps, downloaded and run by the `.cmd` |
| `helper-start.ps1` | Run by the Startup shortcut: checks for a newer release, then starts the helper |
| `SynaflowPrinterHelper.exe` | The helper itself (.NET 8, self-contained single file) |
| `SHA256SUMS.txt` | SHA-256 of the files above — the installer refuses to install if they do not match |

The `.exe` is **not code-signed yet**. Every release lists the SHA-256 of each file so it
can be verified.

## Build from source

```
dotnet publish src/PrinterHelperSetup.csproj -c Release
```

Output: `src/bin/Release/net8.0-windows/win-x64/publish/SynaflowPrinterHelper.exe`

## Licence

The source is published **for inspection only** — it is not open source. You may read it and
build it to verify a release, but not copy, modify, redistribute or reuse it. See [LICENSE](LICENSE).
Third-party components (the .NET runtime, System.Drawing.Common, and SumatraPDF which is
downloaded separately) remain under their own licences — see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Why the downloads live here and not on synaflow.app

Serving an unsigned executable plus a download-and-run script from the same domain as the
web app caused an antivirus URL-reputation database to block the entire app domain.
Keeping the downloads on GitHub means that can never take the POS itself offline again.

---

**ภาษาไทย:** โปรแกรมเล็ก ๆ ที่ร้านลงไว้บนเครื่องคอมหน้าร้าน เพื่อให้หน้าเว็บ Synaflow
พิมพ์ใบเสร็จแบบไม่ต้องกดยืนยันและสั่งเปิดลิ้นชักเก็บเงินได้ ฟังเฉพาะในเครื่องตัวเอง
(`localhost:9999`) รับคำสั่งจาก `synaflow.app` และโดเมนย่อยของ synaflow.app เท่านั้น (เช่น `jknfc.synaflow.app` · ตั้งแต่ v1.0.1)
ติดตั้งจากเมนู **ข้อมูลหลัก → เครื่องพิมพ์** ในระบบ

**ตั้งแต่รุ่น 1.1.0** เครื่องนี้ไปถามงานพิมพ์จากเซิร์ฟเวอร์ของร้านเองได้ด้วย (ไม่ต้องให้เบราว์เซอร์เรียก `localhost`)
⇒ สั่งพิมพ์จากมือถือ/แท็บเล็ตเข้าเครื่องพิมพ์ที่ร้านได้ และไม่ต้องกดอนุญาตทีละเครื่องตามกฎใหม่ของเบราว์เซอร์
สิ่งที่ส่งออกมีแค่ **ชื่อเครื่องคอม · รุ่นของตัวช่วยพิมพ์ · ชื่อเครื่องพิมพ์ในเครื่องนั้น · ผลว่าพิมพ์สำเร็จหรือไม่**
และส่งไปที่เซิร์ฟเวอร์ของร้านเท่านั้น · ติดตั้งเสร็จต้องให้แอดมินกด **อนุมัติ** เครื่องนี้หนึ่งครั้งที่ **ข้อมูลหลัก → เครื่องพิมพ์**
