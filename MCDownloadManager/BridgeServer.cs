using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MCDM;

/// Tiny HTTP bridge on 127.0.0.1 for the browser extensions (same API as the macOS app).
public sealed class BridgeServer
{
    /// From the csproj / `dotnet publish -p:Version=` (CI passes the git tag).
    public static readonly string Version = typeof(BridgeServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public bool Running { get; private set; }
    public string StatusText { get; private set; } = S.T("Not started", "Başlatılmadı");
    /// Last time each browser's extension talked to the bridge (chrome, brave, edge, firefox, other).
    public Dictionary<string, DateTime> Connections { get; } = new();

    readonly DownloadStore _store;
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    TcpListener? _listener;
    int _port;

    public BridgeServer(DownloadStore store) => _store = store;

    public void Start(int port)
    {
        _listener?.Stop();
        _port = Math.Clamp(port, 1024, 65535);
        try
        {
            var l = new TcpListener(IPAddress.Loopback, _port);
            l.Start();
            _listener = l;
            Running = true;
            StatusText = S.T($"Listening on 127.0.0.1:{_port}", $"127.0.0.1:{_port} dinleniyor");
            _ = AcceptLoop(l);
        }
        catch (Exception e)
        {
            Running = false;
            StatusText = S.T($"Error: {e.Message} (port {_port} may be in use)", $"Hata: {e.Message} ({_port} portu kullanımda olabilir)");
        }
    }

    async Task AcceptLoop(TcpListener l)
    {
        while (true)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(); }
            catch { return; }   // listener stopped
            _ = Serve(c);
        }
    }

    sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    async Task Serve(TcpClient client)
    {
        using var _ = client;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var stream = client.GetStream();
            var req = await ReadRequest(stream, cts.Token);
            if (req == null) return;
            var (status, json) = await _ui.InvokeAsync(() => Handle(req));
            await Send(stream, status, json, req.Headers.GetValueOrDefault("origin"), cts.Token);
        }
        catch { }
    }

    static async Task<Request?> ReadRequest(NetworkStream s, CancellationToken ct)
    {
        var buf = new MemoryStream();
        var chunk = new byte[16384];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await s.ReadAsync(chunk, ct);
            if (n == 0 || buf.Length > 64 * 1024) return null;
            buf.Write(chunk, 0, n);
            headerEnd = IndexOf(buf.GetBuffer(), (int)buf.Length, "\r\n\r\n"u8);
        }
        var head = Encoding.UTF8.GetString(buf.GetBuffer(), 0, headerEnd).Split("\r\n");
        var reqLine = head[0].Split(' ');
        if (reqLine.Length < 2) return null;
        var headers = new Dictionary<string, string>();
        foreach (var l in head.Skip(1))
        {
            int c = l.IndexOf(':');
            if (c > 0) headers[l[..c].Trim().ToLowerInvariant()] = l[(c + 1)..].Trim();
        }
        int length = int.TryParse(headers.GetValueOrDefault("content-length"), out var cl) ? cl : 0;
        if (length < 0 || length > 16_000_000) return null;
        int bodyStart = headerEnd + 4;
        while (buf.Length - bodyStart < length)
        {
            int n = await s.ReadAsync(chunk, ct);
            if (n == 0) return null;
            buf.Write(chunk, 0, n);
        }
        var body = buf.GetBuffer().AsSpan(bodyStart, length).ToArray();
        return new Request(reqLine[0].ToUpperInvariant(), reqLine[1], headers, body);
    }

    static int IndexOf(byte[] data, int len, ReadOnlySpan<byte> pattern) => data.AsSpan(0, len).IndexOf(pattern);

    static async Task Send(NetworkStream s, int status, object json, string? origin, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(json);
        var head = new StringBuilder()
            .Append($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append($"Content-Length: {body.Length}\r\n")
            .Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");
        // CORS headers only for extension origins; ordinary web pages get none.
        if (OriginAllowed(origin))
            head.Append($"Access-Control-Allow-Origin: {origin}\r\nVary: Origin\r\n")
                .Append("Access-Control-Allow-Headers: Content-Type, X-MCDM-Extension\r\n")
                .Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n")
                // Chrome Private/Local Network Access preflights for loopback targets.
                .Append("Access-Control-Allow-Private-Network: true\r\nAccess-Control-Allow-Local-Network: true\r\n");
        head.Append("Connection: close\r\n\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        await s.WriteAsync(body, ct);
    }

    /// Only browser extensions may talk to the bridge. Missing, empty or "null" origins are rejected,
    /// so ordinary web pages cannot drive the app.
    public static bool OriginAllowed(string? origin) =>
        !string.IsNullOrEmpty(origin) && origin != "null" &&
        (origin.StartsWith("chrome-extension://") || origin.StartsWith("moz-extension://") || origin.StartsWith("safari-web-extension://"));

    /// Extension requests carry an extension Origin, or — when Chrome omits Origin on GETs covered by host
    /// permissions — no Origin plus X-MCDM-Extension. A web page can't produce the latter: the custom header
    /// forces a CORS preflight whose web Origin is rejected here.
    static bool ExtensionRequest(Request r)
    {
        var origin = r.Headers.GetValueOrDefault("origin");
        if (OriginAllowed(origin)) return true;
        return origin == null && !string.IsNullOrEmpty(r.Headers.GetValueOrDefault("x-mcdm-extension"));
    }

    bool HostAllowed(string? host) =>
        host?.ToLowerInvariant() is string h && (h == $"127.0.0.1:{_port}" || h == $"localhost:{_port}" || h == $"[::1]:{_port}");

    void NoteConnection(Request r)
    {
        var origin = r.Headers.GetValueOrDefault("origin") ?? "";
        string browser = origin.StartsWith("chrome-extension://") ? "chrome" : origin.StartsWith("moz-extension://") ? "firefox" : "other";
        var h = r.Headers.GetValueOrDefault("x-mcdm-extension")?.ToLowerInvariant();
        if (h is "chrome" or "brave" or "edge" or "firefox") browser = h;
        int q = r.Path.IndexOf('?');
        if (q >= 0)
            foreach (var pair in r.Path[(q + 1)..].Split('&'))
                if (pair.StartsWith("browser=")) browser = pair[8..].ToLowerInvariant();
        Connections[browser] = DateTime.Now;
    }

    (int, object) Handle(Request req)
    {
        if (!ExtensionRequest(req) || !HostAllowed(req.Headers.GetValueOrDefault("host")))
            return (403, new { ok = false, error = "forbidden" });
        if (req.Method == "OPTIONS") return (200, new { ok = true });
        NoteConnection(req);
        var path = req.Path.Split('?')[0];

        switch (req.Method, path)
        {
            case ("GET", "/ping"):
                return (200, new
                {
                    ok = true,
                    app = "MCDownloadManager",
                    version = Version,
                    ytdlp = VideoDownloader.LocateYtDlp(_store.Settings.YtdlpPath) != null,
                    active = _store.ActiveCount,
                    captureExtensions = _store.Settings.CaptureExtensions,
                });

            case ("GET", "/downloads"):
                return (200, new
                {
                    ok = true,
                    items = _store.Items.Take(50).Select(i => new
                    {
                        id = i.Id.ToString().ToUpperInvariant(),
                        name = i.FileName,
                        state = i.State.ToString().ToLowerInvariant(),
                        progress = i.Progress,
                        speed = i.Speed,
                        total = i.TotalBytes,
                        received = i.ReceivedBytes,
                        stage = i.Stage,
                    }),
                });

            case ("POST", "/download"):
                try
                {
                    var o = JsonDocument.Parse(req.Body).RootElement;
                    string? Str(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    if (!Uri.TryCreate(Str("url"), UriKind.Absolute, out var url) || (url.Scheme != "http" && url.Scheme != "https"))
                        return (400, new { ok = false, error = "invalid url" });
                    var r = new NewDownloadRequest
                    {
                        Url = url,
                        FileName = Str("filename"),
                        Referer = Str("referer"),
                        UserAgent = Str("userAgent"),
                        CookieHeader = Str("cookieHeader"),
                        Kind = Str("kind") == "video" ? DownloadKind.Video : DownloadKind.File,
                    };
                    if (o.TryGetProperty("cookies", out var ck) && ck.ValueKind == JsonValueKind.Array) r.Cookies = ck.EnumerateArray().ToList();
                    if (o.TryGetProperty("headers", out var hs) && hs.ValueKind == JsonValueKind.Object)
                        foreach (var p in hs.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String) r.ExtraHeaders[p.Name] = p.Value.GetString()!;
                    if (o.TryGetProperty("segments", out var sg) && sg.TryGetInt32(out var n)) r.Segments = Math.Clamp(n, 1, 16);
                    var item = _store.Add(r);
                    return (200, new { ok = true, id = item.Id.ToString().ToUpperInvariant() });
                }
                catch (JsonException) { return (400, new { ok = false, error = "invalid json" }); }

            case ("POST", "/action"):
                try
                {
                    var o = JsonDocument.Parse(req.Body).RootElement;
                    var item = Guid.TryParse(o.GetProperty("id").GetString(), out var id) ? _store.Items.FirstOrDefault(i => i.Id == id) : null;
                    if (item == null) return (400, new { ok = false, error = "id and action required" });
                    switch (o.GetProperty("action").GetString())
                    {
                        case "pause": _store.Pause(item); break;
                        case "resume": _store.Resume(item); break;
                        case "cancel": _store.Cancel(item); break;
                        case "remove": _store.Remove(item); break;
                        default: return (400, new { ok = false, error = "unknown action" });
                    }
                    return (200, new { ok = true });
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
                { return (400, new { ok = false, error = "id and action required" }); }

            default:
                return (404, new { ok = false, error = "not found" });
        }
    }
}
