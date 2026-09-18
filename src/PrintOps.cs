using System.Diagnostics;
using System.Text;

namespace SynaflowPrinterSetup;

/// <summary>
/// ผลของการสั่งพิมพ์หนึ่งครั้ง — ใช้ร่วมกันทั้ง 2 ทาง (พอร์ต 9999 และงานที่ดึงมาจากเซิร์ฟเวอร์)
/// Status มีไว้ให้ฝั่งพอร์ต 9999 ตอบรหัสเดิมเป๊ะ (400/500) โดยไม่ต้องรู้รายละเอียดข้างใน
/// </summary>
internal sealed record PrintResult(bool Ok, int Status, string? Error, string? Detail, string? Note)
{
    public static PrintResult Success(string? note = null) => new(true, 200, null, null, note);
    public static PrintResult Fail(int status, string error, string detail) => new(false, status, error, detail, null);

    /// ข้อความที่คนอ่านรู้เรื่อง สำหรับส่งกลับไปขึ้นบนหน้าจอฝั่งเซิร์ฟเวอร์
    public string Message => string.IsNullOrWhiteSpace(Detail) ? (Error ?? "") : $"{Error}: {Detail}";
}

/// <summary>
/// งานพิมพ์จริง ๆ ทั้ง 4 แบบ — แยกออกมาจาก HelperServer ตอนทำ v1.1.0 (W302)
///
/// 🔴 แยกเพราะตอนนี้มี **2 ทางเข้า** ที่ต้องพิมพ์ด้วยวิธีเดียวกันเป๊ะ:
///      ① หน้าเว็บเรียก http://localhost:9999 (ทางเดิม — เร็ว ใช้ได้ถ้าเบราว์เซอร์ยอม)
///      ② ตัวช่วยพิมพ์ดึงงานจากเซิร์ฟเวอร์เอง (ทางใหม่ — มือถือ/เบราว์เซอร์ที่บล็อก localhost)
///    ถ้าปล่อยให้แต่ละทางเขียนขั้นตอนพิมพ์เอง วันหนึ่งจะแก้ทางเดียวแล้วอีกทางเพี้ยน
///
/// 🪤 ค่า timeout / ลำดับขั้น / ข้อความผิดพลาด ยกมาจากรุ่น 1.0.2 ทั้งดุ้น ห้ามปรับเล่น
///    (ค่าพวกนี้มาจากของจริงหน้าร้าน เช่น ดอตเมตริกซ์พิมพ์ช้ากว่า 20 วิถือว่าเข้าคิวแล้ว)
/// </summary>
internal static class PrintOps
{
    // ── ลิ้นชักเงิน ─────────────────────────────────────────────────────────────
    public static PrintResult Drawer(string printer)
    {
        try
        {
            RawPrint.Send(printer, RawPrint.DrawerKick);
            return PrintResult.Success();
        }
        catch (Exception ex) { return PrintResult.Fail(400, "drawer_failed", ex.Message); }
    }

    // ── ESC/POS ไบต์ดิบ (สลิป 80mm) ─────────────────────────────────────────────
    public static PrintResult Raw(string printer, byte[] bytes)
    {
        try
        {
            RawPrint.Send(printer, bytes);
            return PrintResult.Success();
        }
        catch (Exception ex) { return PrintResult.Fail(400, "print_raw_failed", ex.Message); }
    }

    // ── PDF เงียบผ่าน SumatraPDF ────────────────────────────────────────────────
    public static PrintResult Pdf(string printer, byte[] pdf, string? printSettings)
    {
        var sumatra = ToolFinder.FindSumatra(Logger.Log);
        if (string.IsNullOrEmpty(sumatra))
        {
            Logger.Log("พิมพ์ PDF → ไม่เจอ SumatraPDF");
            return PrintResult.Fail(500, "sumatra_not_found", "ไม่เจอ SumatraPDF และโหลดอัตโนมัติไม่สำเร็จ — เช็คอินเทอร์เน็ต");
        }

        var tmp = Path.Combine(Path.GetTempPath(), $"erp-print-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(tmp, pdf);
            var ps = string.IsNullOrWhiteSpace(printSettings) ? "noscale" : printSettings!;

            // 🪤 งานดอตเมตริกซ์พิมพ์ช้า SumatraPDF อาจไม่จบไว → เกิน 20 วิถือว่าเข้าคิวแล้ว ตอบ ok
            //    (ของเดิมเลือกแบบนี้ไว้ เพราะไม่งั้นปุ่มบนจอค้างทั้งที่งานเข้าคิวไปแล้ว)
            var r = RunTool(sumatra, SumatraArgs(printer, ps, tmp), 20_000);

            if (r.timedOut) return PrintResult.Success("spooled");
            if (r.exitCode != 0)
                return PrintResult.Fail(500, $"sumatra_exit_{r.exitCode}",
                    $"SumatraPDF ล้ม (exit {r.exitCode}) — เช็คว่าเครื่องพิมพ์ '{printer}' มีอยู่และออนไลน์");
            return PrintResult.Success();
        }
        catch (Exception ex) { return PrintResult.Fail(400, "print_pdf_failed", ex.Message); }
        finally { TryDelete(tmp); }
    }

    // ── HTML → PDF (Chrome ไม่เปิดหน้าต่าง) → พิมพ์เงียบ ─────────────────────────
    // ใช้กับป้ายราคา: หน้าต่างพิมพ์ของเบราว์เซอร์หมุนหน้าป้ายแบน ๆ (89×10mm) แต่ Chrome
    // แบบไม่เปิดหน้าต่างเคารพขนาด @page ถูกต้อง ไม่หมุน
    public static PrintResult Html(string printer, string html, string? printSettings)
    {
        var chrome = ToolFinder.FindChrome();
        if (string.IsNullOrEmpty(chrome))
        {
            Logger.Log("พิมพ์ HTML → ไม่เจอ Chrome/Edge");
            return PrintResult.Fail(500, "chrome_not_found", "ไม่เจอ Chrome/Edge — จำเป็นกับการทำ PDF ป้ายราคา");
        }
        var sumatra = ToolFinder.FindSumatra(Logger.Log);
        if (string.IsNullOrEmpty(sumatra))
        {
            Logger.Log("พิมพ์ HTML → ไม่เจอ SumatraPDF");
            return PrintResult.Fail(500, "sumatra_not_found", "ไม่เจอ SumatraPDF และโหลดอัตโนมัติไม่สำเร็จ");
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
                return PrintResult.Fail(500, "chrome_timeout", "Chrome ทำ PDF ไม่เสร็จใน 12 วินาที");
            if (!File.Exists(pdfFile))
                return PrintResult.Fail(500, "chrome_pdf_failed", "Chrome ไม่ได้สร้างไฟล์ PDF ออกมา");

            var ps = string.IsNullOrWhiteSpace(printSettings) ? "noscale" : printSettings!;
            // 🪤 เครื่องพิมพ์ออฟไลน์ทำ SumatraPDF ค้างรอ → ต้องฆ่าที่ 10 วิ ไม่งั้นตัวช่วยพิมพ์
            //    (ทำทีละคำขอ) แขวน แล้วหน้าจอฝั่งเบราว์เซอร์รอไม่จบ
            var r = RunTool(sumatra, SumatraArgs(printer, ps, pdfFile), 10_000);

            if (r.timedOut)
                return PrintResult.Fail(500, "print_timeout",
                    $"SumatraPDF ไม่จบใน 10 วินาที — เครื่องพิมพ์ '{printer}' ต่ออยู่และออนไลน์ไหม");
            if (r.exitCode != 0)
                return PrintResult.Fail(500, $"sumatra_exit_{r.exitCode}",
                    $"SumatraPDF ล้ม (exit {r.exitCode}) — เช็คว่าเครื่องพิมพ์ '{printer}' มีอยู่และออนไลน์");
            return PrintResult.Success();
        }
        catch (Exception ex) { return PrintResult.Fail(400, "print_html_failed", ex.Message); }
        finally
        {
            TryDelete(htmlFile);
            TryDelete(pdfFile);
            try { if (Directory.Exists(udd)) Directory.Delete(udd, true); } catch { }
        }
    }

    // ── ตัวช่วยเล็ก ๆ ────────────────────────────────────────────────────────────

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

    internal record ToolResult(bool timedOut, int exitCode);

    internal static ToolResult RunTool(string exe, string[] args, int timeoutMs)
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

    internal static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
