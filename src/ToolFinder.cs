namespace SynaflowPrinterSetup;

/// <summary>
/// หาโปรแกรมช่วยที่ helper ต้องเรียก — ย้ายมาจาก Find-Sumatra / Find-Chrome ในสคริปต์เดิม
///
/// 🪤 ทั้งสองตัวนี้ยัง "เรียกโปรแกรมอื่น" เหมือนเดิม และ **ตั้งใจคงไว้** —
///    SumatraPDF กับ Chrome เป็นโปรแกรมที่เซ็นใบรับรองถูกต้องและคนทั้งโลกใช้
///    ต่างจาก powershell.exe -ExecutionPolicy Bypass ที่เป็นลายเซ็นมัลแวร์ (W247)
/// </summary>
internal static class ToolFinder
{
    const string SumatraUrl = "https://www.sumatrapdfreader.org/dl/rel/3.5.2/SumatraPDF-3.5.2-64.exe";

    static string? _sumatraCache;
    static string? _chromeCache;

    /// <summary>หา SumatraPDF — ไม่เจอจะโหลดมาเก็บไว้ใน LOCALAPPDATA ครั้งเดียว (เครื่องซ่อมตัวเองได้)</summary>
    public static string FindSumatra(Action<string>? log = null)
    {
        if (_sumatraCache is not null && File.Exists(_sumatraCache)) return _sumatraCache;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var exeDir = AppContext.BaseDirectory;

        var dirs = new[]
        {
            Path.Combine(local, "SumatraPDF"),
            @"C:\Program Files\SumatraPDF",
            @"C:\Program Files (x86)\SumatraPDF",
            exeDir,
            Path.GetDirectoryName(exeDir.TrimEnd(Path.DirectorySeparatorChar)) ?? exeDir,
            Path.Combine(profile, "Downloads"),
            Path.Combine(profile, "Desktop"),
        };

        foreach (var d in dirs)
        {
            if (string.IsNullOrWhiteSpace(d) || !Directory.Exists(d)) continue;
            try
            {
                var hit = Directory.EnumerateFiles(d, "SumatraPDF*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (hit is not null) { _sumatraCache = hit; return hit; }
            }
            catch { /* โฟลเดอร์ที่เข้าไม่ได้ (สิทธิ์/ลิงก์เสีย) ข้ามไป ไม่ใช่เหตุให้ล้มทั้งฟังก์ชัน */ }
        }

        var target = Path.Combine(local, "SumatraPDF", "SumatraPDF.exe");
        if (File.Exists(target)) { _sumatraCache = target; return target; }

        try
        {
            log?.Invoke($"ไม่เจอ SumatraPDF — กำลังโหลดมาเก็บไว้ที่ {target}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            var bytes = http.GetByteArrayAsync(SumatraUrl).GetAwaiter().GetResult();
            File.WriteAllBytes(target, bytes);
            if (File.Exists(target)) { log?.Invoke("โหลด SumatraPDF สำเร็จ"); _sumatraCache = target; return target; }
        }
        catch (Exception ex) { log?.Invoke("โหลด SumatraPDF ไม่สำเร็จ: " + ex.Message); }

        return "";
    }

    /// <summary>หา Chrome (Edge เป็นตัวสำรอง) — ใช้ทำ HTML → PDF แบบไม่เปิดหน้าต่าง สำหรับพิมพ์ป้ายราคา</summary>
    public static string FindChrome()
    {
        if (_chromeCache is not null && File.Exists(_chromeCache)) return _chromeCache;

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var cands = new[]
        {
            Path.Combine(pf,    @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf86,  @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(local, @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(pf,    @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(pf86,  @"Microsoft\Edge\Application\msedge.exe"),
        };

        foreach (var c in cands)
            if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) { _chromeCache = c; return c; }

        return "";
    }

    /// <summary>
    /// ตัด " และอักขระควบคุมออกจากชื่อเครื่องพิมพ์ กัน argument injection เข้า SumatraPDF
    /// (ย้ายมาจาก Get-SafePrinterArg — ด่านจาก M11 audit 2026-07-02 ห้ามถอด)
    /// ชื่อจริงไทย/วงเล็บ/ช่องว่าง/UNC \\SERVER\ชื่อ ไม่มีอักขระพวกนี้ จึงไม่กระทบ
    /// </summary>
    public static string SafePrinterArg(string? name)
    {
        if (name is null) return "";
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
            if (ch != '"' && ch != '\r' && ch != '\n' && ch != '\t') sb.Append(ch);
        return sb.ToString();
    }
}
