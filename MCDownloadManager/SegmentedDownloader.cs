using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace MCDM;

public sealed record ProbeResult(long TotalBytes, bool SupportsRanges, string? SuggestedFileName, string? ContentType, Uri? FinalUrl);

public sealed class DownloadFailure : Exception { public DownloadFailure(string m) : base(m) { } }

public sealed class HttpStatusError : Exception
{
    public int Status { get; }
    public HttpStatusError(int status) : base(S.T($"Server returned HTTP {status}", $"Sunucu HTTP {status} döndürdü")) => Status = status;
}

/// Downloads one file with N parallel HTTP Range requests (falls back to a single stream).
/// The coordinator loop runs on one task; segment workers only touch their own segment slot.
public sealed class SegmentedDownloader
{
    public sealed class Config
    {
        public required Uri Url;
        public Dictionary<string, string> Headers = new();
        public required string PartPath;
        public int SegmentCount = 8;
        public long MinSegmentSize = 512 * 1024;
        public SegmentInfo[] ResumeSegments = Array.Empty<SegmentInfo>();
        public long ResumeTotal = -1;
    }

    /// Raised on a thread-pool thread.
    public event Action<ProbeResult>? Probed;

    /// Live state, polled by the store's UI timer.
    public SegmentInfo[] Segments = Array.Empty<SegmentInfo>();
    public long Received => _single ? Interlocked.Read(ref _singleReceived) : Segments.Sum(s => s.Received);

    readonly Config _cfg;
    readonly CancellationTokenSource _stop = new();
    Uri _requestUrl;
    bool _single;
    long _singleReceived;
    SafeFileHandle? _file;

    const int MaxAttempts = 12;
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    static readonly string[] CredentialHeaders = { "cookie", "authorization", "proxy-authorization" };

    static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,          // redirects are followed by hand so credentials can be dropped cross-origin
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        MaxConnectionsPerServer = 32,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        ConnectCallback = HappyEyeballsConnect,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    /// Like browsers: race IPv6 and IPv4 (v4 starts 250 ms later) so a broken IPv6 route doesn't stall every connect.
    static async ValueTask<Stream> HappyEyeballsConnect(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
        var v6 = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
        var v4 = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task<System.Net.Sockets.Socket> Attempt(IPAddress ip, int delayMs)
        {
            if (delayMs > 0) await Task.Delay(delayMs, race.Token);
            var s = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try { await s.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), race.Token); return s; }
            catch { s.Dispose(); throw; }
        }
        var attempts = new List<Task<System.Net.Sockets.Socket>>();
        if (v6 != null) attempts.Add(Attempt(v6, 0));
        if (v4 != null) attempts.Add(Attempt(v4, v6 != null ? 250 : 0));
        if (attempts.Count == 0) throw new HttpRequestException("No address for " + ctx.DnsEndPoint.Host);
        Exception? last = null;
        while (attempts.Count > 0)
        {
            var t = await Task.WhenAny(attempts);
            attempts.Remove(t);
            if (t.IsCompletedSuccessfully)
            {
                race.Cancel();
                foreach (var other in attempts) _ = other.ContinueWith(o => { if (o.IsCompletedSuccessfully) o.Result.Dispose(); });
                return new System.Net.Sockets.NetworkStream(t.Result, ownsSocket: true);
            }
            last = t.Exception?.InnerException;
        }
        throw last ?? new HttpRequestException("Connect failed");
    }

    public SegmentedDownloader(Config cfg)
    {
        _cfg = cfg;
        _requestUrl = cfg.Url;
    }

    public void Stop() => _stop.Cancel();

    /// Completes when the file is fully written; throws on failure, OperationCanceledException after Stop().
    public async Task RunAsync()
    {
        try
        {
            bool canResume = _cfg.ResumeTotal > 0 && _cfg.ResumeSegments.Length > 0 && File.Exists(_cfg.PartPath);
            if (canResume)
            {
                Segments = (SegmentInfo[])_cfg.ResumeSegments.Clone();
                OpenFile(create: false, preallocate: null);
                if (await RunMultiAsync()) return;
            }
            else
            {
                var probe = await ProbeAsync();
                Probed?.Invoke(probe);
                int count = probe.SupportsRanges ? (int)Math.Max(1, Math.Min(_cfg.SegmentCount, probe.TotalBytes / _cfg.MinSegmentSize)) : 1;
                if (probe.SupportsRanges && count > 1)
                {
                    Segments = MakeSegments(probe.TotalBytes, count);
                    OpenFile(create: true, preallocate: probe.TotalBytes);
                    if (await RunMultiAsync()) return;
                }
            }
            // Single stream (no ranges, small file, or the server ignored Range and answered 200).
            _file?.Dispose();
            Segments = Array.Empty<SegmentInfo>();
            OpenFile(create: true, preallocate: null);
            _single = true;
            await RunSingleAsync();
        }
        finally
        {
            _file?.Dispose();
            _file = null;
        }
    }

    public static SegmentInfo[] MakeSegments(long total, int count)
    {
        long size = total / count;
        return Enumerable.Range(0, count).Select(i =>
        {
            long start = i * size;
            return new SegmentInfo { Start = start, End = i == count - 1 ? total - 1 : start + size - 1 };
        }).ToArray();
    }

    // MARK: - HTTP

    bool SameOrigin(Uri u) =>
        string.Equals(u.Host, _cfg.Url.Host, StringComparison.OrdinalIgnoreCase) && u.Scheme == _cfg.Url.Scheme;

    /// Sends a GET, following redirects by hand. Credentials (Cookie/Authorization) only go to the original origin.
    async Task<HttpResponseMessage> SendAsync(string? range, CancellationToken ct)
    {
        var url = _requestUrl;
        for (int hop = 0; hop < 10; hop++)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            bool allowCred = SameOrigin(url);
            foreach (var (k, v) in _cfg.Headers)
            {
                if (!allowCred && CredentialHeaders.Contains(k.ToLowerInvariant())) continue;
                req.Headers.TryAddWithoutValidation(k, v);
            }
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            req.Headers.CacheControl = new() { NoCache = true };
            if (range != null) req.Headers.TryAddWithoutValidation("Range", range);

            var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)resp.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location is Uri loc)
            {
                resp.Dispose();
                url = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
                if (url.Scheme != "http" && url.Scheme != "https") throw new DownloadFailure("Invalid redirect");
                continue;
            }
            _requestUrl = url;   // later segment requests go straight to the final URL
            return resp;
        }
        throw new DownloadFailure(S.T("Too many redirects", "Çok fazla yönlendirme"));
    }

    async Task<ProbeResult> ProbeAsync()
    {
        using var resp = await SendAsync("bytes=0-0", _stop.Token);
        int status = (int)resp.StatusCode;
        if (status is < 200 or > 299) throw new HttpStatusError(status);

        long total = -1;
        bool ranges = false;
        var cr = resp.Content.Headers.ContentRange;
        if (status == 206 && cr?.Length is long len)
        {
            total = len;
            ranges = true;
        }
        else
        {
            total = resp.Content.Headers.ContentLength ?? -1;
            ranges = total > 0 && resp.Headers.AcceptRanges.Contains("bytes");
        }
        var cd = resp.Content.Headers.ContentDisposition;
        string? name = (cd?.FileNameStar ?? cd?.FileName)?.Trim('"', ' ');
        return new ProbeResult(total, ranges, string.IsNullOrEmpty(name) ? null : name,
            resp.Content.Headers.ContentType?.MediaType, _requestUrl);
    }

    void OpenFile(bool create, long? preallocate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cfg.PartPath)!);
        _file = File.OpenHandle(_cfg.PartPath, create ? FileMode.Create : FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
            FileOptions.Asynchronous, preallocate ?? 0);
        try { File.SetAttributes(_cfg.PartPath, File.GetAttributes(_cfg.PartPath) | FileAttributes.Hidden); } catch { }
        if (preallocate is long n && n > 0) RandomAccess.SetLength(_file, n);
    }

    // MARK: - Multi-segment

    enum Outcome { Done, Fallback, Throttled, Retry }

    /// Returns true when all segments completed, false when the server ignored ranges (caller falls back to single).
    async Task<bool> RunMultiAsync()
    {
        var pending = new List<int>(Enumerable.Range(0, Segments.Length).Where(i => !Segments[i].IsDone));
        var running = new Dictionary<Task<(Outcome, int, Exception?)>, int>();
        var retries = new Dictionary<int, int>();
        int activeLimit = Math.Max(1, _cfg.SegmentCount);
        var throttledUntil = DateTime.MinValue;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);   // cancels siblings on fallback/fail

        try
        {
            while (true)
            {
                var wait = throttledUntil - DateTime.UtcNow;
                if (wait > TimeSpan.Zero && running.Count == 0) await Task.Delay(wait, _stop.Token);
                if (throttledUntil <= DateTime.UtcNow)
                {
                    while (running.Count < activeLimit && pending.Count > 0)
                    {
                        int i = pending[0];
                        pending.RemoveAt(0);
                        if (Segments[i].IsDone) continue;
                        running[RunSegmentAsync(i, linked.Token)] = i;
                    }
                }
                if (running.Count == 0)
                {
                    if (pending.Count == 0) return true;
                    continue;   // throttled, loop waits above
                }

                // Wake at least once a second so segments whose back-off expired start without waiting for a sibling to finish.
                var any = await Task.WhenAny(running.Keys.Cast<Task>().Append(Task.Delay(1000, _stop.Token)));
                _stop.Token.ThrowIfCancellationRequested();
                if (any is not Task<(Outcome, int, Exception?)> finished) continue;
                running.Remove(finished);
                var (outcome, idx, error) = await finished;
                _stop.Token.ThrowIfCancellationRequested();

                switch (outcome)
                {
                    case Outcome.Done:
                        activeLimit = Math.Min(_cfg.SegmentCount, activeLimit + 1);
                        break;
                    case Outcome.Fallback:
                        linked.Cancel();
                        await Task.WhenAll(running.Keys.Select(t => t.ContinueWith(_ => { })));
                        return false;
                    case Outcome.Throttled:
                    case Outcome.Retry:
                        int attempt = retries.GetValueOrDefault(idx) + 1;
                        retries[idx] = attempt;
                        if (attempt > MaxAttempts)
                            throw error ?? new DownloadFailure(S.T($"Segment {idx + 1} could not be completed", $"{idx + 1}. parça tamamlanamadı"));
                        if (outcome == Outcome.Throttled)
                        {
                            // Server throttling (429/503): fewer connections, retry this segment first.
                            activeLimit = Math.Max(1, Math.Min(activeLimit, running.Count) - 1);
                            throttledUntil = DateTime.UtcNow.AddSeconds(Math.Min(15, 1.5 * attempt));
                            pending.Insert(0, idx);
                        }
                        else
                        {
                            var until = DateTime.UtcNow.AddSeconds(Math.Min(10, attempt));
                            if (until > throttledUntil) throttledUntil = until;
                            pending.Add(idx);
                        }
                        break;
                }
            }
        }
        catch
        {
            linked.Cancel();
            await Task.WhenAll(running.Keys.Select(t => t.ContinueWith(_ => { })));
            throw;
        }
    }

    async Task<(Outcome, int, Exception?)> RunSegmentAsync(int i, CancellationToken ct)
    {
        var seg = Segments[i];
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            readCts.CancelAfter(ReadTimeout);
            using var resp = await SendAsync($"bytes={seg.Start + seg.Received}-{seg.End}", readCts.Token);
            int code = (int)resp.StatusCode;
            if (code == 200) return (Outcome.Fallback, i, null);
            if (code is 408 or 425 or 429 or 500 or 502 or 503 or 504) return (Outcome.Throttled, i, new HttpStatusError(code));
            if (code != 206) throw new HttpStatusError(code);

            await using var stream = await resp.Content.ReadAsStreamAsync(readCts.Token);
            var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (!Segments[i].IsDone)
                {
                    readCts.CancelAfter(ReadTimeout);
                    int n = await stream.ReadAsync(buf.AsMemory(), readCts.Token);
                    if (n == 0) break;
                    var s = Segments[i];
                    int take = (int)Math.Min(n, s.Length - s.Received);
                    await RandomAccess.WriteAsync(_file!, buf.AsMemory(0, take), s.Start + s.Received, ct);
                    Segments[i].Received += take;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
            return Segments[i].IsDone ? (Outcome.Done, i, null) : (Outcome.Retry, i, null);
        }
        catch (HttpStatusError) { throw; }
        catch (IOException e) when (!ct.IsCancellationRequested) { return (Outcome.Retry, i, e); }
        catch (HttpRequestException e) when (!ct.IsCancellationRequested) { return (Outcome.Retry, i, e); }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested) { return (Outcome.Retry, i, e); }   // read timeout
    }

    // MARK: - Single stream

    async Task RunSingleAsync()
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        readCts.CancelAfter(ReadTimeout);
        using var resp = await SendAsync(null, readCts.Token);
        int code = (int)resp.StatusCode;
        if (code is < 200 or > 299) throw new HttpStatusError(code);
        // The real GET may differ from the probe (dynamic responses); trust its Content-Length.
        long total = resp.Content.Headers.ContentLength ?? -1;

        await using var stream = await resp.Content.ReadAsStreamAsync(readCts.Token);
        var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                readCts.CancelAfter(ReadTimeout);
                int n;
                try { n = await stream.ReadAsync(buf.AsMemory(), readCts.Token); }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                { throw new DownloadFailure(S.T("Connection timed out", "Bağlantı zaman aşımına uğradı")); }
                if (n == 0) break;
                await RandomAccess.WriteAsync(_file!, buf.AsMemory(0, n), _singleReceived, _stop.Token);
                Interlocked.Add(ref _singleReceived, n);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }

        if (total > 0 && _singleReceived < total)
            throw new DownloadFailure(S.T($"Connection closed early ({_singleReceived}/{total} bytes)",
                $"Bağlantı erken kapandı ({_singleReceived}/{total} bayt)"));
    }
}
