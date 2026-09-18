using System.Text;
using System.Text.Json;

namespace SynaflowPrinterSetup;

/// <summary>ตัวตนของเครื่องนี้ต่อเซิร์ฟเวอร์ — agentId เปิดเผยได้ ส่วน Secret ห้ามหลุดทุกกรณี</summary>
internal sealed record AgentIdentity(string AgentId, string Secret, string ServerUrl, string MachineName);

/// <summary>
/// ไฟล์ %LOCALAPPDATA%\Synaflow\agent.json — ที่เก็บกุญแจของเครื่องนี้ (W302)
///
/// ทำไมต้องเก็บ: เซิร์ฟเวอร์คืน secret ให้ "ครั้งเดียวตอนลงทะเบียน" (ฝั่งนั้นเก็บแต่ค่าแฮช)
/// ทำหายเมื่อไหร่ = ต้องให้แอดมินถอนสิทธิ์เครื่องเดิมแล้วลงทะเบียนใหม่
///
/// 🔴 ไฟล์นี้คือความลับ — อยู่ใต้ %LOCALAPPDATA% ของผู้ใช้คนนั้น (ผู้ใช้อื่นที่ไม่ใช่แอดมิน
///    เข้าไม่ถึงอยู่แล้วตามสิทธิ์มาตรฐานของ Windows) · ห้ามเขียนลงที่อื่น ห้าม log ค่าข้างใน
///
/// 🪤 เครื่องเดียวอาจถูกชี้ไปคนละเซิร์ฟเวอร์ (เช่น ทดสอบที่ localhost แล้วกลับไป synaflow.app)
///    กุญแจของเซิร์ฟเวอร์ A ใช้กับ B ไม่ได้ → จะได้ 401 วนไม่จบแล้วลงทะเบียนซ้ำเป็นพรวน
///    จึงเก็บ serverUrl ไว้ในไฟล์ด้วย · ถ้าเปลี่ยนเซิร์ฟเวอร์ ให้ "พักไฟล์เดิมไว้"
///    เป็น agent.&lt;host&gt;.json แล้วหยิบของเซิร์ฟเวอร์ใหม่มาแทน (สลับกลับไปกลับมาได้ไม่เสียตัวตน)
/// </summary>
internal static class AgentStore
{
    public static string Path_ => Path.Combine(Logger.Folder, "agent.json");

    /// <summary>อ่านตัวตนของเซิร์ฟเวอร์ที่กำลังใช้ — ไม่มี = ยังไม่เคยลงทะเบียน</summary>
    public static AgentIdentity? Load(string server)
    {
        var id = ReadFile(Path_);

        if (id is not null && !SameServer(id.ServerUrl, server))
        {
            // ตัวตนในไฟล์เป็นของอีกเซิร์ฟเวอร์ — พักไว้ก่อน (ไม่ทิ้ง) แล้วหาของเซิร์ฟเวอร์นี้
            Park(id);
            id = null;
        }

        if (id is null)
        {
            var parked = ParkPath(server);
            if (File.Exists(parked))
            {
                try
                {
                    File.Move(parked, Path_, overwrite: true);
                    Logger.Log($"หยิบตัวตนเดิมของ {HostKey(server)} กลับมาใช้");
                    id = ReadFile(Path_);
                }
                catch (Exception ex) { Logger.Log("หยิบตัวตนเดิมกลับมาไม่สำเร็จ: " + ex.Message); }
            }
        }

        if (id is not null && !SameServer(id.ServerUrl, server)) return null;
        return id;
    }

    public static void Save(AgentIdentity id)
    {
        Directory.CreateDirectory(Logger.Folder);
        var json = JsonSerializer.Serialize(new
        {
            serverUrl = id.ServerUrl,
            agentId = id.AgentId,
            secret = id.Secret,
            machineName = id.MachineName,
            savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        }, new JsonSerializerOptions { WriteIndented = true });

        // เขียนไฟล์ชั่วคราวก่อนแล้วค่อยสลับ — ไฟฟ้าดับกลางคันจะได้ไม่เหลือไฟล์ครึ่ง ๆ ที่อ่านไม่ออก
        var tmp = Path_ + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, Path_, overwrite: true);
        Logger.Log($"บันทึกตัวตนเครื่องนี้ไว้ที่ {Path_} (agent #{id.AgentId})");
    }

    /// <summary>กุญแจใช้ไม่ได้แล้วจริง ๆ (เซิร์ฟเวอร์ไม่รู้จัก) — ลบทิ้งเพื่อลงทะเบียนใหม่</summary>
    public static void Forget(string reason)
    {
        try { if (File.Exists(Path_)) File.Delete(Path_); } catch { }
        Logger.Log("ลบกุญแจของเครื่องนี้ทิ้ง: " + reason);
    }

    // ── ภายใน ───────────────────────────────────────────────────────────────────

    static AgentIdentity? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path, new UTF8Encoding(false)));
            var root = doc.RootElement;

            var secret = Str(root, "secret");
            if (secret is null) return null;

            var agentId = Str(root, "agentId") ?? Num(root, "agentId") ?? "";
            var server = Str(root, "serverUrl") ?? CloudAgent.DefaultServer;
            var machine = Str(root, "machineName") ?? Environment.MachineName;
            return new AgentIdentity(agentId, secret, Normalize(server), machine);
        }
        catch (Exception ex)
        {
            Logger.Log("อ่าน agent.json ไม่ได้: " + ex.Message);
            return null;
        }
    }

    static void Park(AgentIdentity id)
    {
        try
        {
            var to = ParkPath(id.ServerUrl);
            File.Move(Path_, to, overwrite: true);
            Logger.Log($"เปลี่ยนเซิร์ฟเวอร์ → พักตัวตนของ {HostKey(id.ServerUrl)} ไว้ก่อน");
        }
        catch (Exception ex) { Logger.Log("พักตัวตนเดิมไม่สำเร็จ: " + ex.Message); }
    }

    static string ParkPath(string server) => Path.Combine(Logger.Folder, $"agent.{HostKey(server)}.json");

    /// ชื่อไฟล์ที่ปลอดภัย: host (+พอร์ตถ้าไม่ใช่ค่ามาตรฐาน) เหลือแต่ a-z 0-9 . -
    internal static string HostKey(string server)
    {
        var host = server;
        if (Uri.TryCreate(server, UriKind.Absolute, out var u))
            host = u.IsDefaultPort ? u.Host : $"{u.Host}-{u.Port}";
        var sb = new StringBuilder(host.Length);
        foreach (var ch in host.ToLowerInvariant())
            sb.Append(ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' ? ch : '_');
        var s = sb.ToString();
        return s.Length == 0 ? "server" : s;
    }

    internal static bool SameServer(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// ตัด / ท้าย และตัวพิมพ์ ให้เทียบกันได้ (https://Synaflow.app/ = https://synaflow.app)
    internal static string Normalize(string url)
    {
        var s = (url ?? "").Trim().TrimEnd('/');
        return s.Length == 0 ? CloudAgent.DefaultServer : s;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString() : null;

    static string? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.ToString() : null;
}
