using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SynaflowPrinterSetup;

/// <summary>
/// ตัวช่วยพิมพ์ — สะพานระหว่างหน้าเว็บกับเครื่องพิมพ์ของเครื่องนี้ (พอร์ต 9999)
///
/// 🔴 ย้ายมาจาก scripts/printer-helper.ps1 แบบ **พฤติกรรมต้องเหมือนเดิมทุกอย่าง**
///    พอร์ตเดิม · ชื่อปลายทางเดิม · รูป JSON เดิม · allowlist เดิม · timeout เดิม
///    ⇒ หน้าเว็บไม่ต้องแก้อะไรเลยสักบรรทัด และสลับกลับไปใช้ .ps1 ได้ถ้าเจอปัญหา
///
///    เหตุที่ย้าย: Kaspersky จับตัวติดตั้งเป็น Trojan เพราะ "เขียนสคริปต์ลงดิสก์ + เรียก
///    powershell.exe -ExecutionPolicy Bypass + ฝังตัว" = ลายเซ็นมัลแวร์ (W247)
///    พิสูจน์แล้ว: สแกนตัวไฟล์เฉย ๆ สะอาด (453 ชิ้น เจอ 0) แต่โดนจับ "ตอนกด"
///    ⇒ ปัญหาอยู่ที่พฤติกรรมขณะรัน ไม่ใช่วิธีห่อไฟล์ ⇒ ต้องไม่มี PowerShell ในวงจรเลย
///
/// 🪤 ตั้งใจทำทีละคำขอ (single-thread) เหมือนสคริปต์เดิม — ของเดิมเลือกแบบนี้แล้วคุม
///    timeout ทุกจุดที่เรียกโปรแกรมอื่น เพื่อไม่ให้คำขอหนึ่งค้างแล้วแขวนทั้งตัว
///    ถ้าเปลี่ยนเป็นหลายเธรดต้องทบทวน timeout ใหม่ทั้งหมด — อย่าเปลี่ยนลอย ๆ
/// </summary>
internal static class HelperServer
{
    public const int Port = 9999;

    /// 🪤 allowlist นี้เป็นด่านกันเว็บมุ่งร้ายสั่งเปิดลิ้นชัก/พิมพ์ (CRIT-1 audit 2026-07-02)
    ///    เดิมเคย echo origin อะไรก็ได้ → เว็บอื่นที่แคชเชียร์เปิดค้างยิง POST ได้ · ห้ามถอด
    static readonly string[] AllowedOrigins =
    {
        "https://synaflow.app",
        "http://localhost:3000",
        "http://127.0.0.1:3000",
    };

    static readonly string Machine = Environment.MachineName;

    public static int Run()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{Port}/");
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");

        try { listener.Start(); }
        catch (Exception ex)
        {
            Log($"เปิดพอร์ต {Port} ไม่ได้ — มีตัวอื่นใช้อยู่หรือเปล่า: {ex.Message}");
            return 1;
        }

        Log($"ตัวช่วยพิมพ์ทำงานแล้ว · เครื่อง {Machine} · พอร์ต {Port}");

        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch (Exception ex) { Log("รับคำขอไม่สำเร็จ: " + ex.Message); continue; }

            try { Handle(ctx); }
            catch (Exception ex)
            {
                Log("จัดการคำขอพลาด: " + ex.Message);
                try { SendJson(ctx.Response, new { error = "internal", detail = ex.Message }, 500); } catch { }
            }
        }
        return 0;
    }

    static void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        // ── CORS + Private Network Access ───────────────────────────────────────
        // หน้า HTTPS (synaflow.app) → http://localhost ต้องมี Allow-Private-Network: true
        // ไม่งั้น Chrome บล็อก preflight ทิ้งตั้งแต่ต้น
        var origin = req.Headers["Origin"];
        var originBlank = string.IsNullOrWhiteSpace(origin);
        var originOk = originBlank || AllowedOrigins.Contains(origin);

        if (!originBlank && originOk)
        {
            res.Headers.Add("Access-Control-Allow-Origin", origin!);
            res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
            res.Headers.Add("Access-Control-Allow-Private-Network", "true");
        }

        // ระบุ origin มาแต่ไม่อยู่ allowlist = เว็บอื่น → ปฏิเสธ **ก่อน** เกิดผลข้างเคียง
        // (ครอบทั้ง preflight และคำขอธรรมดา · ไม่มี origin = เครื่องมือ/ไม่ใช่เบราว์เซอร์ ปล่อยผ่าน)
        if (!originOk)
        {
            res.StatusCode = 403;
            try { res.Close(); } catch { }
            Log($"ปฏิเสธ origin '{origin}' → 403");
            return;
        }

        if (req.HttpMethod == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }

        var path = req.Url?.AbsolutePath ?? "";
        var method = req.HttpMethod;

        if (method == "GET" && path == "/health")
        {
            SendJson(res, new { ok = true, machine = Machine });
        }
        else if (method == "GET" && path == "/printers")
        {
            var printers = RawPrint.InstalledPrinters();
            SendJson(res, new { machine = Machine, printers });
            Log($"GET /printers → {printers.Count} เครื่อง");
        }
        else if (method == "POST" && path == "/cash-drawer")
        {
            var b = ReadBody(req);
            var printer = Str(b, "printerName");
            if (printer is null) { SendJson(res, new { error = "missing_printer" }, 400); return; }
            try
            {
                RawPrint.Send(printer, RawPrint.DrawerKick);
                SendJson(res, new { ok = true });
                Log($"POST /cash-drawer → '{printer}' OK");
            }
            catch (Exception ex)
            {
                SendJson(res, new { error = "drawer_failed", detail = ex.Message }, 400);
                Log($"POST /cash-drawer → พลาด: {ex.Message}");
            }
        }
        else if (method == "POST" && path == "/print-raw")
        {
            var b = ReadBody(req);
            var printer = Str(b, "printerName");
            var b64 = Str(b, "base64");
            if (printer is null || b64 is null) { SendJson(res, new { error = "missing_fields" }, 400); return; }
            try
            {
                var bytes = Convert.FromBase64String(b64);
                RawPrint.Send(printer, bytes);
                SendJson(res, new { ok = true, bytes = bytes.Length });
                Log($"POST /print-raw → '{printer}' {bytes.Length} ไบต์ OK");
            }
            catch (Exception ex)
            {
                SendJson(res, new { error = "print_raw_failed", detail = ex.Message }, 400);
                Log($"POST /print-raw → พลาด: {ex.Message}");
            }
        }
        else if (method == "POST" && path == "/print-pdf")
        {
            HandlePrintPdf(req, res);
        }
        else if (method == "POST" && path == "/print-html")
        {
            HandlePrintHtml(req, res);
        }
        else
        {
            SendJson(res, new { error = "not_found", path }, 404);
        }
    }

    // ── POST /print-pdf — พิมพ์ PDF เงียบผ่าน SumatraPDF ──────────────────────────
    static void HandlePrintPdf(HttpListenerRequest req, HttpListenerResponse res)
    {
        var b = ReadBody(req);
        var printer = Str(b, "printerName");
        var b64 = Str(b, "base64");
        if (printer is null || b64 is null) { SendJson(res, new { error = "missing_fields" }, 400); return; }

        var sumatra = ToolFinder.FindSumatra(Log);
        if (string.IsNullOrEmpty(sumatra))
        {
            SendJson(res, new { error = "sumatra_not_found", detail = "ไม่เจอ SumatraPDF และโหลดอัตโนมัติไม่สำเร็จ — เช็คอินเทอร์เน็ต" }, 500);
            Log("POST /print-pdf → ไม่เจอ SumatraPDF");
            return;
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"erp-print-{Guid.NewGuid():N}.pdf");
        try
        {
            var bytes = Convert.FromBase64String(b64);
            File.WriteAllBytes(tmp, bytes);

            var ps = Str(b, "printSettings") ?? "noscale";
            // 🪤 งานดอตเมตริกซ์พิมพ์ช้า SumatraPDF อาจไม่จบไว → เกิน 20 วิถือว่าเข้าคิวแล้ว ตอบ ok
            //    (ของเดิมเลือกแบบนี้ไว้ เพราะไม่งั้นปุ่มบนจอค้างทั้งที่งานเข้าคิวไปแล้ว)
            var r = RunTool(sumatra, SumatraArgs(printer, ps, tmp), 20_000);

            if (r.timedOut)
            {
                SendJson(res, new { ok = true, bytes = bytes.Length, note = "spooled" });
                Log($"POST /print-pdf → เข้าคิวแล้ว (SumatraPDF เกิน 20 วิ) '{printer}'");
            }
            else if (r.exitCode != 0)
            {
                SendJson(res, new { error = $"sumatra_exit_{r.exitCode}", detail = $"SumatraPDF ล้ม (exit {r.exitCode}) — เช็คว่าเครื่องพิมพ์ '{printer}' มีอยู่และออนไลน์" }, 500);
                Log($"POST /print-pdf → SumatraPDF exit {r.exitCode} ('{printer}')");
            }
            else
            {
                SendJson(res, new { ok = true, bytes = bytes.Length });
                Log($"POST /print-pdf → '{printer}' {bytes.Length} ไบต์ OK");
            }
        }
        catch (Exception ex)
        {
            SendJson(res, new { error = "print_pdf_failed", detail = ex.Message }, 400);
            Log($"POST /print-pdf → พลาด: {ex.Message}");
        }
        finally { TryDelete(tmp); }
    }

    // ── POST /print-html — HTML → PDF (Chrome ไม่เปิดหน้าต่าง) → พิมพ์เงียบ ────────
    // ใช้กับป้ายราคา: หน้าต่างพิมพ์ของเบราว์เซอร์หมุนหน้าป้ายแบน ๆ (89×10mm) แต่ Chrome
    // แบบไม่เปิดหน้าต่างเคารพขนาด @page ถูกต้อง ไม่หมุน
    static void HandlePrintHtml(HttpListenerRequest req, HttpListenerResponse res)
    {
        var b = ReadBody(req);
        var printer = Str(b, "printerName");
        var html = Str(b, "html");
        if (printer is null || html is null) { SendJson(res, new { error = "missing_fields" }, 400); return; }

        var chrome = ToolFinder.FindChrome();
        if (string.IsNullOrEmpty(chrome))
        {
            SendJson(res, new { error = "chrome_not_found", detail = "ไม่เจอ Chrome/Edge — จำเป็นกับการทำ PDF ป้ายราคา" }, 500);
            Log("POST /print-html → ไม่เจอ Chrome/Edge");
            return;
        }
        var sumatra = ToolFinder.FindSumatra(Log);
        if (string.IsNullOrEmpty(sumatra))
        {
            SendJson(res, new { error = "sumatra_not_found", detail = "ไม่เจอ SumatraPDF และโหลดอัตโนมัติไม่สำเร็จ" }, 500);
            Log("POST /print-html → ไม่เจอ SumatraPDF");
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        var htmlFile = Path.Combine(Path.GetTempPath(), $"erp-label-{id}.html");
        var pdfFile = Path.Combine(Path.GetTempPath(), $"erp-label-{id}.pdf");
        var udd = Path.Combine(Path.GetTempPath(), $"erp-chrome-{id}");

        try
        {
            File.WriteAllText(htmlFile, html, new UTF8Encoding(false));
            var fileUrl = "file:///" + htmlFile.Replace('\\', '/');

            var chromeArgs = new[]
            {
                "--headless", "--disable-gpu", "--no-pdf-header-footer",
                "--user-data-dir=" + udd,
                "--print-to-pdf=" + pdfFile,
                fileUrl,
            };
            var cr = RunTool(chrome, chromeArgs, 12_000);
            if (cr.timedOut)
            {
                SendJson(res, new { error = "chrome_timeout", detail = "Chrome ทำ PDF ไม่เสร็จใน 12 วินาที" }, 500);
                Log("POST /print-html → Chrome หมดเวลา");
                return;
            }
            if (!File.Exists(pdfFile))
            {
                SendJson(res, new { error = "chrome_pdf_failed", detail = "Chrome ไม่ได้สร้างไฟล์ PDF ออกมา" }, 500);
                Log("POST /print-html → Chrome ไม่ได้สร้าง PDF");
                return;
            }

            var ps = Str(b, "printSettings") ?? "noscale";
            // 🪤 เครื่องพิมพ์ออฟไลน์ทำ SumatraPDF ค้างรอ → ต้องฆ่าที่ 10 วิ ไม่งั้นตัวช่วยพิมพ์
            //    (ทำทีละคำขอ) แขวน แล้วหน้าจอฝั่งเบราว์เซอร์รอไม่จบ
            var r = RunTool(sumatra, SumatraArgs(printer, ps, pdfFile), 10_000);

            if (r.timedOut)
            {
                SendJson(res, new { error = "print_timeout", detail = $"SumatraPDF ไม่จบใน 10 วินาที — เครื่องพิมพ์ '{printer}' ต่ออยู่และออนไลน์ไหม" }, 500);
                Log($"POST /print-html → SumatraPDF หมดเวลา (เครื่อง '{printer}' ออฟไลน์?)");
            }
            else if (r.exitCode != 0)
            {
                SendJson(res, new { error = $"sumatra_exit_{r.exitCode}", detail = $"SumatraPDF ล้ม (exit {r.exitCode}) — เช็คว่าเครื่องพิมพ์ '{printer}' มีอยู่และออนไลน์" }, 500);
                Log($"POST /print-html → SumatraPDF exit {r.exitCode} ('{printer}')");
            }
            else
            {
                SendJson(res, new { ok = true });
                Log($"POST /print-html → '{printer}' OK");
            }
        }
        catch (Exception ex)
        {
            SendJson(res, new { error = "print_html_failed", detail = ex.Message }, 400);
            Log($"POST /print-html → พลาด: {ex.Message}");
        }
        finally
        {
            TryDelete(htmlFile);
            TryDelete(pdfFile);
            try { if (Directory.Exists(udd)) Directory.Delete(udd, true); } catch { }
        }
    }

    static string[] SumatraArgs(string printer, string printSettings, string file) => new[]
    {
        // 🪤 ส่งเป็น argv แยกชิ้น ไม่ต่อเป็นสตริงเดียว — .NET ใส่เครื่องหมายคำพูดให้เองถูกต้อง
        //    ของเดิมต้องใส่ " เองเพราะ PowerShell ไม่ทำให้ ทำให้ชื่อเครื่องที่มีช่องว่าง
        //    อย่าง "EPSON TM-T82II Receipt" หรือ \\SERVER\Canon G3010 โดนหั่นเป็นหลาย arg
        "-print-to", ToolFinder.SafePrinterArg(printer),
        "-print-settings", printSettings,
        "-silent", "-exit-when-done",
        file,
    };

    record ToolResult(bool timedOut, int exitCode);

    static ToolResult RunTool(string exe, string[] args, int timeoutMs)
    {
        var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new Exception($"เรียก {Path.GetFileName(exe)} ไม่ขึ้น");
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return new ToolResult(true, -1);
        }
        return new ToolResult(false, p.ExitCode);
    }

    // ── ตัวช่วยเล็ก ๆ ────────────────────────────────────────────────────────────

    /// 🪤 อ่านเป็น UTF-8 เสมอ — เบราว์เซอร์ส่ง application/json โดยไม่บอก charset
    ///    ถ้าเชื่อ req.ContentEncoding จะตกไปใช้ ANSI (ไทย = CP874) แล้วชื่อเครื่องพิมพ์ไทยเพี้ยน
    ///    → OpenPrinter หาเครื่องไม่เจอ (บทเรียนเดิมจากสคริปต์ PowerShell)
    static JsonElement? ReadBody(HttpListenerRequest req)
    {
        try
        {
            using var sr = new StreamReader(req.InputStream, new UTF8Encoding(false));
            var raw = sr.ReadToEnd();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return JsonDocument.Parse(raw).RootElement.Clone();
        }
        catch { return null; }
    }

    static string? Str(JsonElement? body, string name)
    {
        if (body is null) return null;
        if (!body.Value.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    static void SendJson(HttpListenerResponse res, object obj, int status = 200)
    {
        try
        {
            var json = JsonSerializer.Serialize(obj);
            var bytes = Encoding.UTF8.GetBytes(json);
            res.StatusCode = status;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.OutputStream.Close();
        }
        catch (Exception ex) { Log("ตอบกลับไม่สำเร็จ (ฝั่งเรียกปิดไปแล้ว?): " + ex.Message); }
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        try { Console.WriteLine(line); } catch { }
        // เขียนลงไฟล์ด้วย เพราะตอนใช้จริงมันรันแบบไม่มีหน้าต่าง ไม่มีใครเห็นคอนโซล
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Synaflow");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "printer-helper.log"), line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }
}
