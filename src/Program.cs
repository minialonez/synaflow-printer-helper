using System.Text;
using System.Text.Json;

namespace SynaflowPrinterSetup;

/// <summary>
/// ตัวช่วยพิมพ์ของ Synaflow — ไฟล์ .exe ตัวเดียวมี 2 โหมด:
///   • <c>serve</c> → เปิดพอร์ต 9999 รอหน้าเว็บเรียก (โหมดที่รันค้างในเครื่องทุกวัน)
///   • ไม่มี argument หรือ <c>test-ui</c> → หน้าต่างภาษาไทยไว้ดูสถานะ + ทดสอบพิมพ์
///
/// 🔴 **ไฟล์นี้ไม่ติดตั้งอะไรเองเด็ดขาด** — ไม่คัดลอกตัวเอง ไม่เขียนรีจิสทรี
///    ไม่สร้างทางลัดเปิดเองตอนบูต (W247 · 12 ก.ย. 2569)
///
///    เหตุผล: รอบก่อนให้ .exe ทำขั้นติดตั้งเอง → Kaspersky System Watcher จับเป็น
///    **PDM:Trojan.Win32.Generic** แล้วลบทิ้ง ("Application performing suspicious
///    activity characteristic of malware")
///    หลักฐานชี้ขาด: ไฟล์ 2 ตัวไบต์เดียวกัน — ตัวที่ทำขั้นติดตั้งโดนเก็บ
///    ส่วนตัวที่แค่เปิดพอร์ตรอพิมพ์ **ไม่โดนแตะเลย** · สแกนไฟล์เฉย ๆ สะอาด 453 ชิ้น เจอ 0
///    ⇒ ตัวกระตุ้นคือ "การฝังตัว" ไม่ใช่ตัวไฟล์
///
///    ขั้นติดตั้งจึงย้ายไปอยู่ที่ scripts/install-printer-helper.ps1 ให้ powershell.exe
///    (ไมโครซอฟท์เซ็นไว้) เป็นคนลงมือ ซึ่งเป็นท่าที่สคริปต์เดิมใช้มาเป็นปีแล้วไม่เคยถูกจับ
///    🪤 ห้ามย้ายโค้ดติดตั้งกลับเข้ามาในไฟล์นี้ ไม่ว่าจะดูสะอาดกว่าแค่ไหน
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var mode = args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal))?.TrimStart('-');

        if (string.Equals(mode, "serve", StringComparison.OrdinalIgnoreCase))
            return HelperServer.Run();

        ApplicationConfiguration.Initialize();
        Application.Run(new StatusForm());
        return 0;
    }
}

/// <summary>
/// หน้าต่างสถานะ + ทดสอบพิมพ์ — เปิดหลังติดตั้งเสร็จ หรือเปิดเองเมื่ออยากตรวจ
/// ไม่ทำอะไรกับการติดตั้งเลย แค่ถาม /health แล้วให้กดทดสอบ
/// </summary>
sealed class StatusForm : Form
{
    const string HelperUrl = "http://localhost:9999";

    readonly TextBox _log = new();
    readonly Button _recheck = new();
    readonly ComboBox _printers = new();
    readonly Button _test = new();
    readonly Label _printerLabel = new();
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public StatusForm()
    {
        Text = "ตัวช่วยพิมพ์ — Synaflow";
        Width = 760;
        Height = 520;
        StartPosition = FormStartPosition.CenterScreen;
        // ฟอนต์ไทยของ Windows เอง — ไม่ระบุจะได้ Segoe UI ที่วางสระ/วรรณยุกต์เพี้ยนบางเครื่อง
        Font = new Font("Leelawadee UI", 10F);
        BackColor = Color.White;

        var title = new Label
        {
            Text = "ตัวช่วยพิมพ์",
            Font = new Font("Leelawadee UI", 16F, FontStyle.Bold),
            ForeColor = Color.FromArgb(20, 102, 200),
            AutoSize = true,
            Location = new Point(24, 20),
        };

        var subtitle = new Label
        {
            Text = "ทำให้สลิปพิมพ์ออกเองและลิ้นชักเงินเด้งเองตอนปิดบิล",
            ForeColor = Color.FromArgb(67, 83, 106),
            AutoSize = false,
            Location = new Point(26, 54),
            Size = new Size(700, 22),
        };

        _recheck.Text = "ตรวจอีกครั้ง";
        _recheck.Location = new Point(26, 86);
        _recheck.Size = new Size(150, 36);
        _recheck.BackColor = Color.FromArgb(20, 102, 200);
        _recheck.ForeColor = Color.White;
        _recheck.FlatStyle = FlatStyle.Flat;
        _recheck.FlatAppearance.BorderSize = 0;
        _recheck.Font = new Font("Leelawadee UI", 10F, FontStyle.Bold);
        _recheck.Click += async (_, _) => await CheckAsync();

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Location = new Point(26, 134);
        _log.Size = new Size(700, 250);
        _log.BackColor = Color.FromArgb(247, 248, 250);
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.Font = new Font("Consolas", 9.5F);
        _log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

        _printerLabel.Text = "เครื่องพิมพ์สลิป:";
        _printerLabel.AutoSize = true;
        _printerLabel.Location = new Point(26, 402);
        _printerLabel.Visible = false;
        _printerLabel.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;

        _printers.Location = new Point(130, 398);
        _printers.Size = new Size(330, 28);
        _printers.DropDownStyle = ComboBoxStyle.DropDownList;
        _printers.Visible = false;
        _printers.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;

        _test.Text = "ทดสอบพิมพ์ + เปิดลิ้นชัก";
        _test.Location = new Point(476, 396);
        _test.Size = new Size(250, 34);
        _test.FlatStyle = FlatStyle.Flat;
        _test.Font = new Font("Leelawadee UI", 10F, FontStyle.Bold);
        _test.Visible = false;
        _test.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        _test.Click += async (_, _) => await RunTestAsync();

        Controls.AddRange(new Control[] { title, subtitle, _recheck, _log, _printerLabel, _printers, _test });

        Shown += async (_, _) => await CheckAsync();
    }

    void Say(string line)
    {
        if (_log.InvokeRequired) { _log.Invoke(() => Say(line)); return; }
        _log.AppendText(line + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    async Task CheckAsync()
    {
        _recheck.Enabled = false;
        _printers.Visible = _printerLabel.Visible = _test.Visible = false;
        _log.Clear();
        Say("กำลังตรวจ...");

        string? machine = null;
        for (var i = 0; i < 8; i++)
        {
            machine = await TryHealthAsync();
            if (machine != null) break;
            await Task.Delay(500);
        }

        if (machine == null)
        {
            Say("");
            Say("❌ ตัวช่วยพิมพ์ยังไม่ทำงานบนเครื่องนี้");
            Say("");
            Say("วิธีแก้:");
            Say("  1. เปิดหน้าเว็บ → ข้อมูลหลัก → เครื่องพิมพ์");
            Say("  2. กดปุ่ม \"ดาวน์โหลดตัวติดตั้ง\"");
            Say("  3. ดับเบิลคลิกไฟล์ที่โหลดมา แล้วรอจนขึ้นว่าเสร็จ");
            Say("");
            Say($"  ดู log ได้ที่ {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Synaflow", "printer-helper.log")}");
            _recheck.Enabled = true;
            _recheck.BackColor = Color.FromArgb(162, 74, 6);
            return;
        }

        Say("");
        Say($"✅ ตัวช่วยพิมพ์ทำงานอยู่ — เครื่องนี้ชื่อ \"{machine}\"");
        _recheck.BackColor = Color.FromArgb(26, 122, 78);

        var printers = await GetPrintersAsync();
        Say("");
        if (printers.Count == 0)
        {
            Say("⚠️ ไม่เจอเครื่องพิมพ์สักตัวในเครื่องนี้");
            Say("   ต่อเครื่องพิมพ์และลงไดรเวอร์ให้เรียบร้อยก่อน แล้วกด \"ตรวจอีกครั้ง\"");
            _recheck.Enabled = true;
            return;
        }

        Say($"เจอเครื่องพิมพ์ {printers.Count} เครื่อง:");
        foreach (var p in printers) Say($"   • {p}");

        _printers.Items.Clear();
        foreach (var p in printers) _printers.Items.Add(p);
        _printers.SelectedIndex = GuessSlipPrinter(printers);
        _printerLabel.Visible = _printers.Visible = _test.Visible = true;

        Say("");
        Say("เลือกเครื่องพิมพ์สลิปด้านล่าง แล้วกด \"ทดสอบพิมพ์ + เปิดลิ้นชัก\"");
        _recheck.Enabled = true;
    }

    /// เดาว่าเครื่องไหนน่าจะเป็นเครื่องพิมพ์สลิป — เดาผิดได้ ผู้ใช้เปลี่ยนเองได้อยู่แล้ว
    /// จึงไม่ใช่การเดาที่อันตราย (ต่างจากการเดาตัวคูณหน่วยสินค้า)
    static int GuessSlipPrinter(List<string> printers)
    {
        string[] hints = { "TM-T", "TM-U", "POS", "Thermal", "Receipt", "80mm", "58mm", "EPSON TM" };
        for (var i = 0; i < printers.Count; i++)
            foreach (var h in hints)
                if (printers[i].Contains(h, StringComparison.OrdinalIgnoreCase)) return i;
        return 0;
    }

    async Task RunTestAsync()
    {
        if (_printers.SelectedItem is not string printer) return;
        _test.Enabled = false;
        Say("");
        Say($"ทดสอบกับ \"{printer}\"...");

        try
        {
            var ok1 = await PostAsync("/print-raw", new { printerName = printer, base64 = Convert.ToBase64String(BuildTestSlip()) });
            Say(ok1 ? "  ✅ ส่งใบทดสอบแล้ว — ดูที่เครื่องพิมพ์" : "  ❌ พิมพ์ไม่สำเร็จ");

            var ok2 = await PostAsync("/cash-drawer", new { printerName = printer });
            Say(ok2 ? "  ✅ สั่งเปิดลิ้นชักแล้ว" : "  ❌ สั่งเปิดลิ้นชักไม่สำเร็จ");

            Say("");
            if (ok1 && ok2)
            {
                Say("ถ้าสลิปออกมาและลิ้นชักเด้ง = เรียบร้อยทุกอย่าง ปิดหน้าต่างนี้ได้เลย");
                Say("ลิ้นชักไม่เด้ง? ตรวจว่าสายลิ้นชัก (หัวเหมือนสายโทรศัพท์) เสียบที่ช่อง DK");
                Say("ก้นเครื่องพิมพ์แน่นดีหรือยัง — ลิ้นชักต่อกับเครื่องพิมพ์ ไม่ได้ต่อกับคอม");
            }
        }
        catch (Exception ex) { Say("  ❌ ผิดพลาด: " + ex.Message); }
        finally { _test.Enabled = true; }
    }

    /// ใบทดสอบเป็นอังกฤษล้วนโดยตั้งใจ — ไทยบนเครื่องพิมพ์ความร้อนต้องส่งเป็นภาพหรือ
    /// ตั้งหน้ารหัสก่อน ซึ่งเป็นคนละเรื่องกับที่กำลังทดสอบ ถ้าออกมาเป็นตัวยึกยือจะเข้าใจผิด
    /// ว่าติดตั้งไม่สำเร็จทั้งที่สำเร็จ (ระบบจริงส่งสลิปเป็นภาพอยู่แล้ว)
    static byte[] BuildTestSlip()
    {
        var b = new List<byte>();
        void Raw(params byte[] x) => b.AddRange(x);
        void Txt(string s) => b.AddRange(Encoding.ASCII.GetBytes(s));

        Raw(0x1B, 0x40);                    // ESC @   reset
        Raw(0x1B, 0x61, 0x01);              // ESC a 1 center
        Raw(0x1B, 0x21, 0x30);              // ESC ! 48 double size
        Txt("SYNAFLOW\n");
        Raw(0x1B, 0x21, 0x00);              // ESC ! 0  normal
        Txt("printer setup OK\n");
        Raw(0x1B, 0x61, 0x00);              // ESC a 0 left
        Txt("\n--------------------------------\n");
        Txt($"PC   : {Environment.MachineName}\n");
        Txt($"time : {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        Txt("--------------------------------\n\n");
        Txt("This slip was printed by the\n");
        Txt("Synaflow printer helper.\n\n");
        Raw(0x1B, 0x64, 0x04);              // ESC d 4  feed so the cut clears the text
        Raw(0x1D, 0x56, 0x42, 0x00);        // GS V B 0 partial cut
        return b.ToArray();
    }

    async Task<string?> TryHealthAsync()
    {
        try
        {
            var json = await _http.GetStringAsync(HelperUrl + "/health");
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("machine", out var m) ? m.GetString() : "";
        }
        catch { return null; }
    }

    async Task<List<string>> GetPrintersAsync()
    {
        var list = new List<string>();
        try
        {
            var json = await _http.GetStringAsync(HelperUrl + "/printers");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("printers", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var p in arr.EnumerateArray())
                    if (p.GetString() is { Length: > 0 } s) list.Add(s);
        }
        catch { /* ไม่มีรายชื่อก็ปล่อยว่าง — ข้างบนจัดการเคสนี้ไว้แล้ว */ }
        return list;
    }

    async Task<bool> PostAsync(string path, object body)
    {
        try
        {
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var res = await _http.PostAsync(HelperUrl + path, content);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
