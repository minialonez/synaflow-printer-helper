using System.Runtime.InteropServices;

namespace SynaflowPrinterSetup;

/// <summary>
/// ส่ง "ไบต์ดิบ" เข้าคิวงานพิมพ์ของ Windows — ใช้พิมพ์ ESC/POS และเด้งลิ้นชักเงิน
///
/// 🔴 โค้ดก้อนนี้ย้ายมาจาก scripts/printer-helper.ps1 ตรง ๆ ซึ่งของเดิม**เป็น C# อยู่แล้ว**
///    (ฝังเป็นสตริงแล้ว Add-Type คอมไพล์ตอนรัน) ⇒ ย้ายมาแล้วพฤติกรรมเหมือนเดิมเป๊ะ
///    เหตุที่ย้าย: Kaspersky จับตัวติดตั้งเป็น Trojan เพราะมันเขียนสคริปต์ลงดิสก์แล้วเรียก
///    powershell.exe -ExecutionPolicy Bypass ซึ่งเป็นลายเซ็นมัลแวร์ · พิสูจน์แล้วว่า
///    สแกนตัวไฟล์เฉย ๆ สะอาด (453 ชิ้น เจอ 0) แต่โดนจับตอนรัน ⇒ ต้องตัด PowerShell ออก (W247)
/// </summary>
internal static class RawPrint
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOCINFO
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDataType;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenPrinterW")]
    private static extern bool OpenPrinter(string pName, out IntPtr hPrinter, IntPtr pDefault);

    [DllImport("winspool.drv")] private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "StartDocPrinterW")]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, ref DOCINFO di);

    [DllImport("winspool.drv")] private static extern bool EndDocPrinter(IntPtr hPrinter);
    [DllImport("winspool.drv")] private static extern bool StartPagePrinter(IntPtr hPrinter);
    [DllImport("winspool.drv")] private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    public static void Send(string printerName, byte[] data)
    {
        if (!OpenPrinter(printerName, out var h, IntPtr.Zero))
            throw new Exception($"OpenPrinter failed: {Marshal.GetLastWin32Error()} for '{printerName}'");
        try
        {
            var di = new DOCINFO { pDocName = "ERP-Helper", pDataType = "RAW" };
            if (!StartDocPrinter(h, 1, ref di))
                throw new Exception($"StartDocPrinter failed: {Marshal.GetLastWin32Error()}");
            try
            {
                if (!StartPagePrinter(h)) throw new Exception("StartPagePrinter failed");
                var ptr = Marshal.AllocHGlobal(data.Length);
                try
                {
                    Marshal.Copy(data, 0, ptr, data.Length);
                    if (!WritePrinter(h, ptr, data.Length, out var written) || written != data.Length)
                        throw new Exception($"WritePrinter: wrote {written}/{data.Length}");
                }
                finally { Marshal.FreeHGlobal(ptr); }
                EndPagePrinter(h);
            }
            finally { EndDocPrinter(h); }
        }
        finally { ClosePrinter(h); }
    }

    /// <summary>คำสั่งเด้งลิ้นชัก ESC p 0 50 250 — ค่าเดียวกับสคริปต์เดิมเป๊ะ อย่าเปลี่ยนตัวเลข</summary>
    public static byte[] DrawerKick => new byte[] { 0x1B, 0x70, 0x00, 0x32, 0xFA };

    /// <summary>รายชื่อเครื่องพิมพ์ในเครื่องนี้ (เทียบเท่า Get-Printer ของสคริปต์เดิม)</summary>
    public static List<string> InstalledPrinters()
    {
        var list = new List<string>();
        foreach (string? p in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
            if (!string.IsNullOrWhiteSpace(p)) list.Add(p);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }
}
