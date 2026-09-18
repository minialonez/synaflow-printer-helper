using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SynaflowPrinterSetup;

/// <summary>
/// ตัวดึงงานพิมพ์จากเซิร์ฟเวอร์ (W302 · v1.1.0) — "เครื่องนี้ถามเซิร์ฟเวอร์เองว่ามีอะไรให้พิมพ์ไหม"
///
/// 🔴 ทำไมต้องกลับทิศ: Edge 143+/Chrome 141+ บังคับขออนุญาต Local Network Access
///    หน้าเว็บ https จะเรียก http://localhost:9999 ต้องกดอนุญาตทีละเครื่อง (หรือวางนโยบายองค์กร)
///    ⇒ กลับทิศเป็น "ตัวช่วยพิมพ์ออกไปถามเอง" = ลูกค้าไม่ต้องตั้งอะไรต่อเครื่องอีก
///      และสั่งพิมพ์จากมือถือ/แท็บเล็ตเข้าเครื่องพิมพ์ที่ร้านได้ด้วย
///
/// 🪤 ทางเดิม (พอร์ต 9999) **ยังอยู่ครบและเป็นทางหลักเมื่อใช้ได้** เพราะเร็วกว่า
///    ตัวนี้เป็นทางสำรองที่ไม่ต้องตั้งค่า ไม่ใช่ตัวแทน — ห้ามถอดพอร์ต 9999 ออก
///
/// สัญญากับเซิร์ฟเวอร์ (ตกลงไว้ใน w302-spec.md ห้ามเปลี่ยนข้างเดียว):
///   POST /api/print-agent/register            {machineName, version, printers[]} → {agentId, secret}
///   GET  /api/print-agent/poll?wait=25&amp;version=..&amp;printers=ชื่อ1&amp;printers=ชื่อ2
///                                             Bearer secret → 200 {id, kind, printerName, printSettings, bytes} / 204 ไม่มีงาน
///                                             (เซิร์ฟเวอร์รับ POST ด้วย body {version, printers[]} เหมือนกัน — ใช้เป็นทางถอยถ้า GET โดน 405)
///   GET  /api/print-agent/jobs/{id}/payload   Bearer secret → ไบต์ของงาน
///   POST /api/print-agent/jobs/{id}/result    Bearer secret → {ok, error}
///   ผิดพลาด: 401 agent_unknown · 403 agent_pending · 403 agent_revoked · 409 agent_exists (ตอนลงทะเบียน)
///
/// 🔴 secret ห้าม log ทุกกรณี — บอก Logger.KeepSecretOut() ทันทีที่ได้มา แล้วทุกข้อความ
///    ที่ผ่าน Logger จะถูกกวาดออกให้ · และห้าม log body ของ /register (มี secret อยู่ในนั้น)
/// </summary>
internal static class CloudAgent
{
    public const string DefaultServer = "https://synaflow.app";

    /// เพดานไฟล์งาน 20 MB ตามสเปค — ใหญ่กว่านี้ไม่โหลด ตอบกลับว่าใหญ่เกินไปแทนที่จะกินแรมจนเครื่องค้าง
    public const int MaxPayloadBytes = 20 * 1024 * 1024;

    const int WaitSeconds = 25;

    // สถานะ (ใช้ทั้งใน /health และหน้าต่างทดสอบ)
    const string StOff = "off", StDuplicate = "duplicate", StStarting = "starting", StRegistering = "registering",
                 StPending = "pending", StLinked = "linked", StRevoked = "revoked", StConflict = "conflict",
                 StUnsupported = "unsupported", StThrottled = "throttled", StOffline = "offline";

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    static readonly object Gate = new();

    static string _server = DefaultServer;
    static string _state = StOff;
    static string _detail = "";
    static bool _running;
    static bool _postPoll;          // เซิร์ฟเวอร์ตอบ 405 กับ GET → สลับไปยิงแบบ POST
    static bool _clockChecked;
    static int _fails;              // ติดต่อไม่ได้ติดกันกี่ครั้ง (ใช้คิดเวลาถอย)
    static int _authFails;          // โดนปฏิเสธกุญแจติดกันกี่ครั้ง
    static bool _everWorked;        // กุญแจชุดนี้เคยได้รับคำตอบที่แปลว่า "รู้จักเรา" มาก่อนหรือยัง
    static int _printed;
    static string _lastState = "";
    static long _lastLogTick;
    static long _registerNotBefore;
    static List<string> _printers = new();
    static long _printersReadTick;
    static string _printersSent = "";
    static long _printersSentTick;
    static Mutex? _only;

    public static bool Running => _running;
    public static string Server => _server;
    public static string State { get { lock (Gate) return _state; } }
    public static string Detail { get { lock (Gate) return _detail; } }
    public static int Printed => Volatile.Read(ref _printed);
    public static string Thai => Explain(State, Detail);

    /// <summary>
    /// เริ่มดึงงาน — เรียกครั้งเดียวตอนเข้าโหมด serve
    /// enabled=false (--no-cloud) = ไม่ออกเน็ตเลย ใช้เฉพาะพอร์ต 9999 เหมือนรุ่น 1.0.x
    /// </summary>
    public static void Start(string server, bool enabled)
    {
        if (!enabled)
        {
            Set(StOff, "ปิดไว้ด้วย --no-cloud");
            Logger.Log("โหมดรับงานผ่านเซิร์ฟเวอร์: ปิดไว้ (--no-cloud)");
            return;
        }

        _server = AgentStore.Normalize(server);
        if (!Uri.TryCreate(_server, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
        {
            Set(StOff, $"ที่อยู่เซิร์ฟเวอร์ใช้ไม่ได้: {server}");
            Logger.Log($"ที่อยู่เซิร์ฟเวอร์ใช้ไม่ได้ ({server}) — ปิดโหมดรับงานผ่านเซิร์ฟเวอร์");
            return;
        }

        if (!TakeSingleInstance()) return;

        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"SynaflowPrinterHelper/{AppInfo.Version}");
        Http.DefaultRequestHeaders.ExpectContinue = false;

        _running = true;
        Set(StStarting, "");
        Logger.Log($"โหมดรับงานผ่านเซิร์ฟเวอร์: เปิด · {_server}");

        var t = new Thread(() => LoopAsync().GetAwaiter().GetResult())
        {
            IsBackground = true,
            Name = "synaflow-cloud-agent",
        };
        t.Start();
    }

    /// 🪤 เปิดตัวช่วยพิมพ์ 2 ตัวบนเครื่องเดียว (เช่น ทางลัด Startup + กดเปิดเอง) แล้วทั้งคู่ดึงงาน
    ///    = บิลพิมพ์ซ้ำ หรือครึ่งหนึ่งหายไปอยู่กับตัวที่ไม่มีใครดู · ตัวที่จับ mutex ได้เท่านั้นที่ดึงงาน
    static bool TakeSingleInstance()
    {
        foreach (var name in new[] { "Global\\SynaflowPrinterHelperAgent", "Local\\SynaflowPrinterHelperAgent" })
        {
            try
            {
                var m = new Mutex(false, name);
                bool got;
                try { got = m.WaitOne(0); }
                catch (AbandonedMutexException) { got = true; }   // ตัวเดิมตายกลางคัน = ของว่างแล้ว
                if (got) { _only = m; return true; }

                m.Dispose();
                Set(StDuplicate, "");
                Logger.Log("มีตัวช่วยพิมพ์อีกตัวรับงานจากเซิร์ฟเวอร์อยู่แล้วบนเครื่องนี้ — ตัวนี้จะไม่ดึงงาน (กันพิมพ์ซ้ำ)");
                return false;
            }
            catch (UnauthorizedAccessException) { /* Global อาจถูกนโยบายห้าม → ลองชื่อ Local ต่อ */ }
            catch (Exception ex) { Logger.Log("จองสิทธิ์ตัวเดียวไม่สำเร็จ: " + ex.Message); return true; }
        }
        return true;
    }

    // ── วงรอบหลัก ───────────────────────────────────────────────────────────────

    static async Task LoopAsync()
    {
        while (true)
        {
            int nap;
            try
            {
                var id = AgentStore.Load(_server);
                if (id is null)
                {
                    nap = await RegisterAsync();
                }
                else
                {
                    Logger.KeepSecretOut(id.Secret);
                    nap = await PollAsync(id);
                }
            }
            catch (Exception ex)
            {
                // 🔴 เธรดนี้ห้ามตาย — ตายแล้วเครื่องนั้นเงียบไปจนกว่าจะรีสตาร์ท โดยไม่มีใครรู้
                Trouble(StOffline, "ผิดพลาดไม่คาดคิด: " + Short(ex.Message));
                nap = Backoff();
            }
            if (nap > 0) await Task.Delay(nap);
        }
    }

    // ── ลงทะเบียนเครื่อง ────────────────────────────────────────────────────────

    static async Task<int> RegisterAsync()
    {
        var left = _registerNotBefore - Environment.TickCount64;
        if (left > 0) return (int)Math.Min(5 * 60_000, Math.Max(5_000, left));

        Set(StRegistering, "");
        var body = JsonSerializer.Serialize(new
        {
            machineName = Environment.MachineName,
            version = AppInfo.Version,
            printers = Printers(),
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, _server + "/api/print-agent/register")
        {
            Content = new StringContent(body, new UTF8Encoding(false), "application/json"),
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        HttpResponseMessage res;
        try { res = await Http.SendAsync(req, cts.Token); }
        catch (Exception ex)
        {
            Trouble(StOffline, "ลงทะเบียนไม่ได้: " + Short(ex.Message));
            return Backoff();
        }

        using (res)
        {
            var code = (int)res.StatusCode;

            if (code is 200 or 201)
            {
                // 🔴 body ก้อนนี้มี secret — ห้าม log ทั้งก้อนเด็ดขาด ไม่ว่าจะ debug อยู่ก็ตาม
                var text = await ReadSomeAsync(res);
                string? agentId = null, secret = null;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    agentId = Val(doc.RootElement, "agentId", "id") ?? "";
                    secret = Val(doc.RootElement, "secret", "token");
                }
                catch { }

                if (string.IsNullOrWhiteSpace(secret))
                {
                    Trouble(StUnsupported, "เซิร์ฟเวอร์ตอบ 200 แต่ไม่มี secret มาด้วย");
                    return 10 * 60_000;
                }

                Logger.KeepSecretOut(secret);
                AgentStore.Save(new AgentIdentity(agentId ?? "", secret!, _server, Environment.MachineName));
                _fails = 0;
                _authFails = 0;
                // 🪤 กันแถวรออนุมัติงอกเป็นพรวน: ถ้าวันหนึ่งกุญแจใช้ไม่ได้วน ๆ (เซิร์ฟเวอร์เพี้ยน)
                //    ลงทะเบียนใหม่ได้เร็วสุดชั่วโมงละครั้ง · ครั้งแรกหลังกุญแจหายยังทำได้ทันที
                _registerNotBefore = Environment.TickCount64 + 60 * 60_000;
                Set(StPending, "");
                return 1_000;
            }

            var err = ErrCode(await ReadSomeAsync(res));

            if (code == 409 || err is "agent_exists" or "machine_exists")
            {
                // ชื่อเครื่องนี้มีอยู่แล้วฝั่งเซิร์ฟเวอร์ แต่กุญแจในเครื่องหาย (ลงวินโดวส์ใหม่ / ลบไฟล์)
                Set(StConflict, "");
                return 10 * 60_000;
            }
            if (code == 404)
            {
                Set(StUnsupported, "");
                return 10 * 60_000;
            }
            Trouble(StOffline, $"ลงทะเบียนไม่สำเร็จ (HTTP {code} {err})");
            return Backoff();
        }
    }

    // ── ถามงาน (long poll) ──────────────────────────────────────────────────────

    static async Task<int> PollAsync(AgentIdentity id)
    {
        var url = _server + "/api/print-agent/poll" + PollQuery(out var printersSent);

        using var req = new HttpRequestMessage(_postPoll ? HttpMethod.Post : HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", id.Secret);
        if (_postPoll)
            req.Content = new StringContent(PollBody(), new UTF8Encoding(false), "application/json");

        // รอสั้นกว่าที่ขอไม่ได้ — เผื่อเวลาเดินทางไว้อีก 20 วิ แล้วค่อยถือว่าเน็ตมีปัญหา
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(WaitSeconds + 20));
        HttpResponseMessage res;
        try { res = await Http.SendAsync(req, cts.Token); }
        catch (Exception ex)
        {
            Trouble(StOffline, Short(ex.Message));
            return Backoff();
        }

        using (res)
        {
            CheckClock(res);
            var code = (int)res.StatusCode;

            if (code is 200 or 204)
            {
                _fails = 0;
                _authFails = 0;
                _everWorked = true;
                if (printersSent is not null) { _printersSent = printersSent; _printersSentTick = Environment.TickCount64; }
                Set(StLinked, "");

                if (code == 204) return Nudge();

                var job = ParseJob(await ReadSomeAsync(res));
                if (job is null) return Nudge();         // 200 แต่ไม่มีงานจริง = เหมือน 204

                await RunJobAsync(id, job);
                return 0;                                 // มีงานแล้วรีบถามต่อทันที เผื่อมีใบถัดไปรออยู่
            }

            var body = await ReadSomeAsync(res);
            var err = ErrCode(body);

            if (code == 403 && err is "agent_pending" or "pending")
            {
                // ยังไม่มีใครกดอนุมัติ — ถามต่อแบบช้า ๆ และเขียนล็อกครั้งเดียว (Set กันซ้ำให้แล้ว)
                _fails = 0;
                _authFails = 0;
                _everWorked = true;
                Set(StPending, "");
                return 30_000 + Jitter(5_000);
            }

            if (err is "agent_revoked" or "revoked")
            {
                _fails = 0;
                _authFails = 0;
                _everWorked = true;
                Set(StRevoked, "");
                // ไม่ลบกุญแจทิ้ง: แอดมินอาจกดอนุมัติกลับมา แล้วกุญแจเดิมใช้ได้เลย
                return 5 * 60_000;
            }

            if (code is 401 or 403)
            {
                _authFails++;
                Set(StOffline, $"เซิร์ฟเวอร์ไม่รับกุญแจ (HTTP {code} {err})");
                // 🪤 อย่าเพิ่งทิ้งกุญแจเพราะพลาดครั้งเดียว (เซิร์ฟเวอร์อาจกำลัง deploy อยู่)
                //    ทิ้งเมื่อโดนปฏิเสธติดกัน 3 ครั้งเท่านั้น แล้วค่อยลงทะเบียนใหม่
                //    (RegisterAsync คุมไว้แล้วว่าลงทะเบียนใหม่ได้เร็วสุดชั่วโมงละครั้ง)
                if (_authFails >= 3)
                {
                    AgentStore.Forget($"เซิร์ฟเวอร์ปฏิเสธกุญแจ 3 ครั้งติด (HTTP {code} {err})");
                    _authFails = 0;
                    // กุญแจนี้เคยใช้ได้จริงมาก่อน (เซิร์ฟเวอร์ลืมเรา เช่น กู้ฐานข้อมูลกลับ) = ลงทะเบียนใหม่ได้เลย
                    // ถ้าไม่เคยใช้ได้เลย ปล่อยให้ตัวกันลงทะเบียนรัว ๆ (ชั่วโมงละครั้ง) ทำงานตามเดิม
                    if (_everWorked) { _everWorked = false; _registerNotBefore = 0; }
                    return 5_000;
                }
                return 60_000;
            }

            if (code == 405 && !_postPoll)
            {
                _postPoll = true;
                Logger.Log("เซิร์ฟเวอร์ไม่รับ GET /poll → สลับไปถามแบบ POST");
                return 1_000;
            }

            if (code == 404)
            {
                Set(StUnsupported, "");
                return 10 * 60_000;
            }

            if (code == 429)
            {
                var wait = (int)(res.Headers.RetryAfter?.Delta?.TotalMilliseconds ?? 60_000);
                Set(StThrottled, "");
                return Math.Clamp(wait, 5_000, 10 * 60_000) + Jitter(3_000);
            }

            Trouble(StOffline, $"เซิร์ฟเวอร์ตอบ HTTP {code} {Short(err.Length > 0 ? err : body, 120)}");
            return Backoff();
        }
    }

    // ── ทำงานพิมพ์ 1 ใบ ─────────────────────────────────────────────────────────

    sealed record Job(string Id, string Kind, string Printer, string? Settings, long Bytes);

    static async Task RunJobAsync(AgentIdentity id, Job job)
    {
        Logger.Log($"ได้งาน #{job.Id} · {job.Kind} · เครื่องพิมพ์ '{job.Printer}'" + (job.Bytes > 0 ? $" · {job.Bytes:N0} ไบต์" : ""));

        PrintResult r;
        try
        {
            if (string.IsNullOrWhiteSpace(job.Printer))
                r = PrintResult.Fail(400, "no_printer", "งานนี้ไม่ได้ระบุชื่อเครื่องพิมพ์");
            else if (job.Kind == "cash_drawer")
                r = PrintOps.Drawer(job.Printer);
            else if (job.Kind is not ("raw" or "pdf" or "html"))
                r = PrintResult.Fail(400, "unknown_kind", $"ตัวช่วยพิมพ์รุ่น {AppInfo.Version} ไม่รู้จักงานชนิด '{job.Kind}' — อัปเดตตัวช่วยพิมพ์");
            else if (job.Bytes > MaxPayloadBytes)
                r = PrintResult.Fail(413, "payload_too_large", $"งานใหญ่ {job.Bytes:N0} ไบต์ เกินเพดาน {MaxPayloadBytes:N0} ไบต์ — ไม่ได้โหลดมา");
            else
            {
                var data = await DownloadAsync(id, job);
                r = job.Kind switch
                {
                    "raw" => PrintOps.Raw(job.Printer, data),
                    "pdf" => PrintOps.Pdf(job.Printer, data, job.Settings),
                    "html" => PrintOps.Html(job.Printer, Text(data), job.Settings),
                    _ => PrintResult.Fail(400, "unknown_kind", job.Kind),
                };
            }
        }
        catch (Exception ex)
        {
            // 🪤 พิมพ์พังยังไงก็ต้องตอบผลกลับไป ไม่งั้นงานค้างเป็น "taken" จนหมดอายุ
            //    แล้วคนหน้าร้านเห็นแค่ "กำลังพิมพ์" ค้างอยู่อย่างนั้นโดยไม่รู้ว่าเกิดอะไรขึ้น
            r = Explode(ex);
        }

        if (r.Ok)
        {
            Interlocked.Increment(ref _printed);
            Logger.Log($"งาน #{job.Id} → พิมพ์แล้ว" + (r.Note == "spooled" ? " (เข้าคิวเครื่องพิมพ์แล้ว)" : ""));
        }
        else Logger.Log($"งาน #{job.Id} → ไม่สำเร็จ: {r.Message}");

        await ReportAsync(id, job, r);
    }

    /// แปลง exception เป็นผลที่ฝั่งเซิร์ฟเวอร์อ่านรู้เรื่อง — ข้อความที่เราโยนเองขึ้นต้นด้วยรหัส
    /// (เช่น "payload_too_large: …") ให้ใช้รหัสนั้นตรง ๆ จะได้ไม่กลายเป็น helper_exception ไปหมด
    static PrintResult Explode(Exception ex)
    {
        var msg = Short(ex.Message, 400);
        foreach (var code in new[] { "payload_too_large", "download_failed" })
            if (msg.StartsWith(code + ":", StringComparison.Ordinal))
                return PrintResult.Fail(code == "payload_too_large" ? 413 : 502, code, msg[(code.Length + 1)..].Trim());
        return PrintResult.Fail(500, "helper_exception", msg);
    }

    static async Task<byte[]> DownloadAsync(AgentIdentity id, Job job)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_server}/api/print-agent/jobs/{Uri.EscapeDataString(job.Id)}/payload");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", id.Secret);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!res.IsSuccessStatusCode)
            throw new Exception($"download_failed: โหลดไฟล์งานไม่ได้ (HTTP {(int)res.StatusCode})");

        var len = res.Content.Headers.ContentLength;
        if (len is > MaxPayloadBytes)
            throw new Exception($"payload_too_large: ไฟล์งาน {len:N0} ไบต์ เกินเพดาน {MaxPayloadBytes:N0} ไบต์");

        using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buf, cts.Token)) > 0)
        {
            ms.Write(buf, 0, n);
            // 🪤 เชื่อ Content-Length อย่างเดียวไม่ได้ (ไม่มีมาก็ได้ / โกหกก็ได้) — ต้องนับเองด้วย
            //    ไม่งั้นไฟล์ยักษ์กินแรมเครื่องแคชเชียร์จนค้างกลางการขาย
            if (ms.Length > MaxPayloadBytes)
                throw new Exception($"payload_too_large: ไฟล์งานเกินเพดาน {MaxPayloadBytes:N0} ไบต์");
        }
        return ms.ToArray();
    }

    static async Task ReportAsync(AgentIdentity id, Job job, PrintResult r)
    {
        var payload = JsonSerializer.Serialize(new
        {
            ok = r.Ok,
            error = r.Ok ? null : Short(r.Message, 400),
            errorCode = r.Ok ? null : r.Error,
            note = r.Note,
            version = AppInfo.Version,
        });

        int[] waits = { 1_000, 3_000, 8_000, 20_000 };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_server}/api/print-agent/jobs/{Uri.EscapeDataString(job.Id)}/result")
                {
                    Content = new StringContent(payload, new UTF8Encoding(false), "application/json"),
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", id.Secret);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var res = await Http.SendAsync(req, cts.Token);
                if (res.IsSuccessStatusCode) return;

                var code = (int)res.StatusCode;
                if (code is 400 or 401 or 403 or 404 or 409 or 410)
                {
                    // เซิร์ฟเวอร์ไม่รับผลใบนี้แล้ว (หมดอายุ/ถูกจ่ายให้เครื่องอื่น) — ส่งซ้ำก็ไม่ช่วย
                    Logger.Log($"งาน #{job.Id}: ส่งผลกลับไม่ได้ (HTTP {code}) — ข้ามไป");
                    return;
                }
                throw new Exception($"HTTP {code}");
            }
            catch (Exception ex)
            {
                if (attempt >= waits.Length)
                {
                    Logger.Log($"งาน #{job.Id}: ส่งผลกลับไม่สำเร็จหลังลองใหม่ {waits.Length} ครั้ง ({Short(ex.Message, 120)})");
                    return;
                }
                await Task.Delay(waits[attempt]);
            }
        }
    }

    // ── ตัวช่วยเล็ก ๆ ────────────────────────────────────────────────────────────

    static string PollQuery(out string? printersSent)
    {
        var q = new StringBuilder();
        q.Append("?wait=").Append(WaitSeconds);
        q.Append("&version=").Append(Uri.EscapeDataString(AppInfo.Version));
        q.Append("&machine=").Append(Uri.EscapeDataString(Environment.MachineName));

        printersSent = null;
        // รายชื่อเครื่องพิมพ์เกาะไปกับการถามงาน (สเปคข้อ 3) — แต่ส่งเมื่อ "เปลี่ยน" หรือทุก 5 นาที
        // ไม่ใช่ทุก 25 วินาที เพราะ query ยาว ๆ ทุกครั้งเปลืองเปล่า ๆ
        var list = PrintersForQuery();
        // ต่อเป็นสตริงเดียวไว้เทียบว่า "รายชื่อเปลี่ยนไหม" เท่านั้น ไม่ได้ส่งค่านี้ออกไปที่ไหน
        var signature = string.Join("|", list);
        if (signature != _printersSent || Environment.TickCount64 - _printersSentTick > 5 * 60_000)
        {
            printersSent = signature;
            // 🔴 รูปแบบนี้ต้องตรงกับฝั่งเซิร์ฟเวอร์เป๊ะ: printers=ชื่อ1&printers=ชื่อ2 (ซ้ำหลายตัว)
            //    PrintAgentController รับ string[] แล้วยังเผื่อแบบคั่น | ไว้ด้วย
            //    🪤 เคยจะส่งเป็น JSON array ก้อนเดียว — ฝั่งนั้นจะได้เครื่องพิมพ์ชื่อ ["A","B"] มา 1 ตัว
            //       แล้วดรอปดาวน์เลือกเครื่องพิมพ์ในหน้าตั้งค่าจะมีขยะโผล่โดยไม่มีใครรู้ว่ามาจากไหน
            foreach (var p in list) q.Append("&printers=").Append(Uri.EscapeDataString(p));
        }
        return q.ToString();
    }

    static string PollBody() => JsonSerializer.Serialize(new
    {
        wait = WaitSeconds,
        version = AppInfo.Version,
        machineName = Environment.MachineName,
        printers = Printers(),
    });

    static List<string> Printers()
    {
        if (_printers.Count > 0 && Environment.TickCount64 - _printersReadTick < 60_000) return _printers;
        try { _printers = RawPrint.InstalledPrinters(); }
        catch (Exception ex) { Logger.Log("อ่านรายชื่อเครื่องพิมพ์ไม่ได้: " + ex.Message); }
        _printersReadTick = Environment.TickCount64;
        return _printers;
    }

    /// 🪤 ชื่อเครื่องพิมพ์ไทยพอ escape ใส่ URL แล้วยาวขึ้นราว 9 เท่า (ตัวละ %XX สามชุด)
    ///    query ยาวเกินไปพร็อกซี/เว็บเซิร์ฟเวอร์บางตัวตัดทิ้งเงียบ ๆ จึงคุมไว้ราว 3.5 KB
    ///    เครื่องพิมพ์ที่เกินมาไม่ส่ง — ดีกว่าโดนตัดทั้งคำขอแล้วไม่ได้งานพิมพ์เลย
    static List<string> PrintersForQuery()
    {
        var take = new List<string>();
        var len = 0;
        foreach (var p in Printers())
        {
            var add = Uri.EscapeDataString(p).Length + 10;   // 10 = "&printers="
            if (len + add > 3_500) break;
            take.Add(p);
            len += add;
        }
        return take;
    }

    static Job? ParseJob(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("job", out var inner) && inner.ValueKind == JsonValueKind.Object)
                root = inner;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var jid = Val(root, "id", "jobId");
            if (string.IsNullOrWhiteSpace(jid)) return null;

            var kind = (Val(root, "kind", "type") ?? "").Trim().ToLowerInvariant();
            var printer = Val(root, "printerName", "printer") ?? "";
            var settings = Val(root, "printSettings", "settings");
            long.TryParse(Val(root, "bytes", "size") ?? "0", out var bytes);
            return new Job(jid!, kind, printer, settings, bytes);
        }
        catch { return null; }
    }

    /// รับได้ทั้งค่าที่เป็นสตริงและตัวเลข (id เป็น int ฝั่งฐานข้อมูล แต่บาง endpoint ส่งเป็นสตริง)
    static string? Val(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            if (!e.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            else if (v.ValueKind == JsonValueKind.Number) return v.ToString();
        }
        return null;
    }

    static string ErrCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e)
                && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    static async Task<string> ReadSomeAsync(HttpResponseMessage res)
    {
        try
        {
            var s = await res.Content.ReadAsStringAsync();
            return s.Length > 8192 ? s[..8192] : s;
        }
        catch { return ""; }
    }

    /// HTML ที่โหลดมาเป็นไบต์ — ตัด BOM ออกก่อน ไม่งั้น Chrome เห็นอักขระแปลกหน้าแรก
    static string Text(byte[] data)
    {
        var s = new UTF8Encoding(false).GetString(data);
        return s.Length > 0 && s[0] == '﻿' ? s[1..] : s;
    }

    /// 🪤 ทุกจังหวะเวลาในไฟล์นี้นับจาก TickCount64 (ตัวจับเวลาของเครื่อง) ไม่ใช่ DateTime
    ///    เครื่องหน้าร้านตั้งนาฬิกาเพี้ยน/ถูกซิงก์กระโดดกลางวัน แล้ว DateTime ลบกันติดลบได้
    ///    ⇒ รอบถามงานจะเพี้ยนตามทันที · TickCount64 ไม่สนใจว่านาฬิกาจะถูกหรือผิด
    static void CheckClock(HttpResponseMessage res)
    {
        if (_clockChecked) return;
        var d = res.Headers.Date;
        if (d is null) return;
        _clockChecked = true;

        var diff = (DateTimeOffset.UtcNow - d.Value).TotalMinutes;
        if (Math.Abs(diff) >= 3)
            Logger.Log($"⏰ นาฬิกาเครื่องนี้ต่างจากเซิร์ฟเวอร์ {diff:0} นาที — การพิมพ์ไม่กระทบ " +
                       "(จังหวะเวลาทั้งหมดใช้ตัวจับเวลาของเครื่อง) แต่เวลาที่เห็นในล็อกจะคลาดเท่านี้");
    }

    static int Nudge() => 200 + Jitter(800);

    /// ถอยเป็นเท่าตัว 2→4→8...→60 วินาที + สุ่มอีกนิด
    /// 🪤 ที่ต้องสุ่ม: ร้านหนึ่งมี 4 เครื่อง เน็ตหลุดพร้อมกัน แล้วกลับมาถามพร้อมกันเป๊ะทุกครั้ง
    ///    = เซิร์ฟเวอร์โดนกระแทกเป็นชุด ๆ · สุ่มนิดเดียวก็กระจายออกจากกันแล้ว
    static int Backoff()
    {
        _fails = Math.Min(_fails + 1, 6);
        var ms = Math.Min(60_000, 1_000 * (int)Math.Pow(2, _fails));
        return ms + Jitter(ms / 4);
    }

    static int Jitter(int max) => max <= 0 ? 0 : Random.Shared.Next(max);

    static string Short(string? s, int max = 200)
    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    static void Trouble(string state, string detail)
    {
        Set(state, detail);
    }

    /// <summary>
    /// เปลี่ยนสถานะ + เขียนล็อก — เขียน "ครั้งเดียวต่อสถานะ" แล้วย้ำทุก 10 นาที
    /// 🪤 ไม่มีตัวกันนี้: รออนุมัติทั้งคืน = ล็อก 2,800 บรรทัดเหมือนกันหมด จนหาบรรทัดจริงไม่เจอ
    /// </summary>
    static void Set(string state, string detail)
    {
        lock (Gate) { _state = state; _detail = detail; }

        var changed = state != _lastState;
        if (!changed && Environment.TickCount64 - _lastLogTick < 10 * 60_000) return;
        _lastState = state;
        _lastLogTick = Environment.TickCount64;
        Logger.Log("เซิร์ฟเวอร์: " + Explain(state, detail));
    }

    static string Explain(string state, string detail)
    {
        var msg = state switch
        {
            StOff => "ปิดโหมดรับงานผ่านเซิร์ฟเวอร์ไว้",
            StDuplicate => "มีตัวช่วยพิมพ์อีกตัวรับงานอยู่แล้วบนเครื่องนี้",
            StStarting => "กำลังเริ่ม…",
            StRegistering => $"กำลังลงทะเบียนเครื่องนี้กับ {_server}",
            StPending => "ลงทะเบียนแล้ว — รอแอดมินกดอนุมัติที่ ⚙️ ตั้งค่า → เครื่องพิมพ์",
            StLinked => $"เชื่อมกับ {_server} แล้ว · พร้อมรับงานพิมพ์",
            StRevoked => "เครื่องนี้ถูกถอนสิทธิ์ — ให้แอดมินอนุมัติใหม่ในระบบ",
            StConflict => "ชื่อเครื่องนี้ลงทะเบียนไว้แล้ว แต่กุญแจในเครื่องหาย — ให้แอดมินถอนสิทธิ์เครื่องเดิม แล้วเปิดตัวช่วยพิมพ์ใหม่",
            StUnsupported => "เซิร์ฟเวอร์ยังไม่รองรับโหมดนี้ — ใช้ทางเดิมผ่าน localhost ได้ตามปกติ",
            StThrottled => "เซิร์ฟเวอร์ขอให้ถามช้าลงชั่วคราว",
            StOffline => "ติดต่อเซิร์ฟเวอร์ไม่ได้ — ทางเดิมผ่าน localhost ยังใช้ได้ตามปกติ",
            _ => state,
        };
        return string.IsNullOrWhiteSpace(detail) ? msg : $"{msg} ({detail})";
    }
}
