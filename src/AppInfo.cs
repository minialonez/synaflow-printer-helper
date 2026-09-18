using System.Reflection;

namespace SynaflowPrinterSetup;

/// <summary>
/// รุ่นของตัวช่วยพิมพ์ — อ่านจากตัวไฟล์เอง ไม่ประกาศเลขรุ่นซ้ำในโค้ด
///
/// 🪤 ที่เดียวที่กำหนดเลขรุ่นคือ &lt;Version&gt; ใน PrinterHelperSetup.csproj
///    ถ้าเขียนเลขรุ่นไว้ในโค้ดด้วย จะมี 2 ที่ให้ลืมแก้ แล้วเซิร์ฟเวอร์จะเห็นรุ่นผิด
///    (helper-start.ps1 เทียบรุ่นจาก ProductVersion ของไฟล์ .exe — ต้องเป็นตัวเดียวกัน)
/// </summary>
internal static class AppInfo
{
    /// <summary>เช่น "1.1.0" — ตัดส่วน "+&lt;commit&gt;" ที่ .NET ต่อท้ายให้ออก</summary>
    public static string Version { get; } = Read();

    static string Read()
    {
        var asm = typeof(AppInfo).Assembly;
        var raw = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                  ?? asm.GetName().Version?.ToString()
                  ?? "0.0.0";
        var plus = raw.IndexOf('+');
        if (plus > 0) raw = raw[..plus];
        return raw.Trim();
    }
}
