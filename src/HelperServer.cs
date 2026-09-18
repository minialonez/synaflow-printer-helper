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
///
/// v1.1.0 (W302): เพิ่ม "ตัวดึงงานจากเซิร์ฟเวอร์" (CloudAgent) ทำงานคู่กันอีกเธรด
///    ⇒ พอร์ต 9999 **ไม่เปลี่ยนอะไรเลย** ทั้งปลายทาง รูป JSON รหัสผิดพลาด และ allowlist
///      (เพิ่มแค่ฟิลด์ version + cloud ใน /health ซึ่งเป็นการ "เติม" ไม่ใช่ "เปลี่ยน")
///    ⇒ ขั้นตอนพิมพ์จริงย้ายไป PrintOps.cs ให้ 2 ทางใช้ตัวเดียวกัน จะได้ไม่เพี้ยนแยกกัน
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

    /// v1.0.1 (14 ก.ย. 2569): รับซับโดเมนของ synaflow.app ด้วย (เช่น https://jknfc.synaflow.app)
    ///    ระบบย้ายจาก synaflow.app ไปโดเมนย่อยของแต่ละบริษัท — รุ่น 1.0.0 ปฏิเสธ → พิมพ์บิล/เปิดลิ้นชักไม่ได้
    ///    จำกัดแค่ https + ชื่อย่อยชั้นเดียวของ synaflow.app (โดเมนของเราเท่านั้น คนอื่นจดชื่อย่อยใต้โดเมนเราไม่ได้)
    /// 🪤 ห้ามใช้ EndsWith("synaflow.app") ลอย ๆ — "https://evilsynaflow.app" จะผ่าน
    internal static bool IsAllowedOrigin(string origin)
    {
        if (AllowedOrigins.Contains(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort) return false;
        if (origin != $"https://{u.Host}") return false;   // มี path/พอร์ต/ตัวพิมพ์เพี้ยน = ไม่ใช่ origin จริง
        const string suffix = ".synaflow.app";
        if (!u.Host.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var label = u.Host[..^suffix.Length];
        return label.Length is > 0 and <= 63
            && label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
            && label[0] != '-' && label[^1] != '-';
    }

    static readonly string Machine = Environment.MachineName;

    /// <param name="server">ที่อยู่เซิร์ฟเวอร์สำหรับโหมดดึงงาน (ค่าตั้งต้น https://synaflow.app)</param>
    /// <param name="cloud">false = ไม่ออกเน็ตเลย ทำตัวเหมือนรุ่น 1.0.x ทุกอย่าง</param>
    public static int Run(string server, bool cloud)
    {
        // เริ่มตัวดึงงานก่อน — ต่อให้พอร์ต 9999 เปิดไม่ได้ (โปรแกรมอื่นยึดอยู่) เครื่องนี้ก็ยังพิมพ์ได้
        CloudAgent.Start(server, cloud);

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{Port}/");
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");

        try { listener.Start(); }
        catch (Exception ex)
        {
            Log($"เปิดพอร์ต {Port} ไม่ได้ — มีตัวอื่นใช้อยู่หรือเปล่า: {ex.Message}");

            // 🪤 v1.1.0: เปิดพอร์ตไม่ได้ ไม่ได้แปลว่าพิมพ์ไม่ได้อีกต่อไป — ถ้าตัวดึงงานทำงานอยู่
            //    ให้อยู่ต่อด้วยทางนั้น · แต่ถ้าตัวดึงงานก็ไม่ได้ทำงาน (= มีตัวช่วยพิมพ์อีกตัวครบชุด
            //    อยู่แล้วบนเครื่องนี้) ต้องออกไปเลย ไม่งั้นจะเหลือโปรเซสซอมบี้ค้างไว้เฉย ๆ
            if (!CloudAgent.Running)
            {
                Log("ไม่มีทางรับงานเหลือแล้ว — ปิดตัวเอง");
                return 1;
            }
            Log("ทำงานต่อด้วยโหมดดึงงานจากเซิร์ฟเวอร์อย่างเดียว");
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        Log($"ตัวช่วยพิมพ์ทำงานแล้ว · รุ่น {AppInfo.Version} · เครื่อง {Machine} · พอร์ต {Port}");

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
        var originOk = originBlank || IsAllowedOrigin(origin!);

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
            // 🪤 ฟิลด์ ok/machine ต้องคงอยู่ชื่อเดิมเป๊ะ — usePrinterHelper.ts อ่าน 2 ตัวนี้
            //    v1.1.0 เติม version + cloud (สถานะโหมดดึงงาน) ให้หน้าตั้งค่าเอาไปแสดงได้ · ไม่มี secret
            SendJson(res, new
            {
                ok = true,
                machine = Machine,
                version = AppInfo.Version,
                cloud = new
                {
                    enabled = CloudAgent.Running,
                    status = CloudAgent.State,
                    message = CloudAgent.Thai,
                    server = CloudAgent.Server,
                    printed = CloudAgent.Printed,
                },
            });
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

            var r = PrintOps.Drawer(printer);
            if (r.Ok)
            {
                SendJson(res, new { ok = true });
                Log($"POST /cash-drawer → '{printer}' OK");
            }
            else
            {
                SendJson(res, new { error = r.Error, detail = r.Detail }, r.Status);
                Log($"POST /cash-drawer → พลาด: {r.Detail}");
            }
        }
        else if (method == "POST" && path == "/print-raw")
        {
            var b = ReadBody(req);
            var printer = Str(b, "printerName");
            var b64 = Str(b, "base64");
            if (printer is null || b64 is null) { SendJson(res, new { error = "missing_fields" }, 400); return; }

            byte[] bytes;
            try { bytes = Convert.FromBase64String(b64); }
            catch (Exception ex)
            {
                SendJson(res, new { error = "print_raw_failed", detail = ex.Message }, 400);
                Log($"POST /print-raw → พลาด: {ex.Message}");
                return;
            }

            var r = PrintOps.Raw(printer, bytes);
            if (r.Ok)
            {
                SendJson(res, new { ok = true, bytes = bytes.Length });
                Log($"POST /print-raw → '{printer}' {bytes.Length} ไบต์ OK");
            }
            else
            {
                SendJson(res, new { error = r.Error, detail = r.Detail }, r.Status);
                Log($"POST /print-raw → พลาด: {r.Detail}");
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
    // 🔴 v1.1.0: ขั้นตอนพิมพ์ย้ายไป PrintOps (ใช้ร่วมกับงานที่ดึงมาจากเซิร์ฟเวอร์)
    //    ไฟล์นี้เหลือหน้าที่เดียวคือแปลงผลเป็น JSON รูปเดิมเป๊ะ — หน้าเว็บไม่ต้องแก้อะไร
    static void HandlePrintPdf(HttpListenerRequest req, HttpListenerResponse res)
    {
        var b = ReadBody(req);
        var printer = Str(b, "printerName");
        var b64 = Str(b, "base64");
        if (printer is null || b64 is null) { SendJson(res, new { error = "missing_fields" }, 400); return; }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (Exception ex)
        {
            SendJson(res, new { error = "print_pdf_failed", detail = ex.Message }, 400);
            Log($"POST /print-pdf → พลาด: {ex.Message}");
            return;
        }

        var r = PrintOps.Pdf(printer, bytes, Str(b, "printSettings"));

        if (r.Ok && r.Note == "spooled")
        {
            SendJson(res, new { ok = true, bytes = bytes.Length, note = "spooled" });
            Log($"POST /print-pdf → เข้าคิวแล้ว (SumatraPDF เกิน 20 วิ) '{printer}'");
        }
        else if (r.Ok)
        {
            SendJson(res, new { ok = true, bytes = bytes.Length });
            Log($"POST /print-pdf → '{printer}' {bytes.Length} ไบต์ OK");
        }
        else
        {
            SendJson(res, new { error = r.Error, detail = r.Detail }, r.Status);
            Log($"POST /print-pdf → พลาด: {r.Message}");
        }
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

        var r = PrintOps.Html(printer, html, Str(b, "printSettings"));
        if (r.Ok)
        {
            SendJson(res, new { ok = true });
            Log($"POST /print-html → '{printer}' OK");
        }
        else
        {
            SendJson(res, new { error = r.Error, detail = r.Detail }, r.Status);
            Log($"POST /print-html → พลาด: {r.Message}");
        }
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

    /// v1.1.0: ล็อกไปรวมที่ Logger (ไฟล์เดิม printer-helper.log) เพราะตอนนี้มี 2 เธรดเขียน
    static void Log(string msg) => Logger.Log(msg);
}
