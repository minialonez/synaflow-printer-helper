using System.Text;

namespace SynaflowPrinterSetup;

/// <summary>
/// ที่เดียวที่เขียนล็อก — ทั้งฝั่งพอร์ต 9999 และฝั่งดึงงานจากเซิร์ฟเวอร์ (v1.1.0)
/// เขียนไฟล์เดิม %LOCALAPPDATA%\Synaflow\printer-helper.log เหมือนรุ่นก่อนทุกอย่าง
/// (คำแนะนำเวลาโทรถามปัญหาชี้ที่ไฟล์นี้ — ห้ามย้าย)
///
/// 🔴 ความลับของเครื่อง (bearer) ห้ามโผล่ในล็อกเด็ดขาด · บอก KeepSecretOut() ไว้ครั้งเดียว
///    แล้วทุกข้อความที่ผ่าน Log() จะถูกแทนที่ให้อัตโนมัติ — กันวันที่ใครเผลอ log ทั้ง response
/// 🪤 มี 2 เธรดเขียนพร้อมกันได้แล้ว (ตัวรับพอร์ต + ตัวดึงงาน) จึงต้องมี lock
///    ไม่งั้นบางบรรทัดหายหรือปนกันตอนที่อยากอ่านมากที่สุด
/// </summary>
internal static class Logger
{
    static readonly object Gate = new();
    static string? _secret;

    /// เกิน 2 MB แล้วขึ้นไฟล์ใหม่ — ตัวดึงงานเขียนทุกวัน ปล่อยไว้ไฟล์โตไม่มีที่สิ้นสุด
    const long MaxBytes = 2L * 1024 * 1024;

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Synaflow");

    public static string LogFile => Path.Combine(Folder, "printer-helper.log");

    public static void KeepSecretOut(string? secret)
    {
        // สั้นเกินไป = ไม่ใช่ความลับจริง และถ้าเอาไปแทนที่จะทำให้ข้อความอ่านไม่รู้เรื่อง
        _secret = string.IsNullOrWhiteSpace(secret) || secret!.Length < 12 ? null : secret;
    }

    public static string Scrub(string msg)
    {
        var s = _secret;
        return s is null || msg.Length == 0 ? msg : msg.Replace(s, "***");
    }

    public static void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {Scrub(msg)}";
        try { Console.WriteLine(line); } catch { }

        // เขียนลงไฟล์ด้วย เพราะตอนใช้จริงมันรันแบบไม่มีหน้าต่าง ไม่มีใครเห็นคอนโซล
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var f = LogFile;
                var fi = new FileInfo(f);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var old = Path.Combine(Folder, "printer-helper.1.log");
                    try { if (File.Exists(old)) File.Delete(old); File.Move(f, old); } catch { }
                }
                File.AppendAllText(f, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
