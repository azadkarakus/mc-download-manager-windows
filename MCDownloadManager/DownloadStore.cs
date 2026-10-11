using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MCDM;

public sealed class NewDownloadRequest
{
    public required Uri Url;
    public string? FileName, Referer, UserAgent, CookieHeader;
    public List<JsonElement>? Cookies;
    public DownloadKind Kind = DownloadKind.File;
    public Dictionary<string, string> ExtraHeaders = new();
    public int? Segments;
}

/// All downloads, the queue and persistence. Used only on the UI thread.
public sealed class DownloadStore
{
    public ObservableCollection<DownloadItem> Items { get; } = new();
    public readonly AppSettings Settings;

    /// UI hooks (set by App).
    public Action<DownloadItem>? Added, Completed;
    public Func<bool>? AskInstallTools;

    readonly Dictionary<Guid, SegmentedDownloader> _downloaders = new();
    readonly Dictionary<Guid, Process> _videos = new();
    /// yt-dlp downloads several files (video, audio): bytes of earlier phases, last value, approx total.
    readonly Dictionary<Guid, (long Base, long Last, long Approx)> _videoPhase = new();
    readonly HashSet<Guid> _cookieless = new();
    readonly List<Guid> _toolWaiters = new();
    bool _toolsDeclined;
    readonly Dictionary<Guid, (long Bytes, DateTime Time)> _samples = new();
    readonly DispatcherTimer _timer;
    bool _dirty;
    int _tick;
    static string StorePath => Path.Combine(AppSettings.DataDir, "downloads.json");

    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    public DownloadStore(AppSettings settings)
    {
        Settings = settings;
        Load();
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(0.5), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher.CurrentDispatcher);
    }

    // MARK: - Queries

    /// Final folder: base folder, plus a category subfolder when enabled.
    public string DestinationDirectory(DownloadItem item) =>
        Settings.OrganizeByCategory ? Path.Combine(item.Directory, S.CategoryLabel(item.Category)) : item.Directory;

    public int ActiveCount => Items.Count(i => i.IsActive);
    public double TotalSpeed => Items.Where(i => i.State == DownloadState.Downloading).Sum(i => i.Speed);
    public double? OverallProgress
    {
        get
        {
            var m = Items.Where(i => i.IsActive && i.TotalBytes > 0).ToList();
            long total = m.Sum(i => i.TotalBytes);
            return total > 0 ? (double)m.Sum(i => Math.Min(i.ReceivedBytes, i.TotalBytes)) / total : null;
        }
    }

    DownloadItem? Find(Guid id) => Items.FirstOrDefault(i => i.Id == id);

    // MARK: - Adding

    public DownloadItem Add(NewDownloadRequest req)
    {
        var headers = new Dictionary<string, string>(req.ExtraHeaders);
        if (!string.IsNullOrEmpty(req.Referer)) headers["Referer"] = req.Referer;
        if (!string.IsNullOrEmpty(req.UserAgent)) headers["User-Agent"] = req.UserAgent;
        if (!string.IsNullOrEmpty(req.CookieHeader)) headers["Cookie"] = req.CookieHeader;
        headers.TryAdd("User-Agent", DefaultUserAgent);

        var name = string.IsNullOrEmpty(req.FileName) ? FileNameFrom(req.Url) : req.FileName;
        if (req.Kind == DownloadKind.Video) name = req.Url.Host;
        var item = new DownloadItem
        {
            Url = req.Url.AbsoluteUri,
            FileName = Sanitize(name),
            Directory = Settings.DownloadDirectory,
            Kind = req.Kind,
            Headers = headers,
            RequestedSegments = req.Segments ?? 0,
        };
        if (req.Kind == DownloadKind.Video && req.Cookies is { Count: > 0 }) item.CookieFile = WriteCookieFile(req.Cookies, item.Id);
        Directory.CreateDirectory(item.Directory);
        Items.Insert(0, item);
        _dirty = true;
        if (Settings.AutoStart) PumpQueue();
        Added?.Invoke(item);
        return item;
    }

    public void AddUrls(string text, DownloadKind kind, int? segments = null)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Uri.TryCreate(line, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
                Add(new NewDownloadRequest { Url = u, Kind = kind, Segments = segments });
    }

    // MARK: - Queue

    public void PumpQueue()
    {
        int slots = Math.Max(0, Settings.MaxConcurrent - ActiveCount);
        foreach (var item in Items.Reverse().Where(i => i.State == DownloadState.Queued).ToList())
        {
            if (slots-- <= 0) break;
            if (item.Kind == DownloadKind.Video) StartVideo(item); else StartFile(item);
        }
    }

    void StartFile(DownloadItem item)
    {
        bool resumable = item.SupportsRanges && item.TotalBytes > 0 && item.Segments.Length > 0 && File.Exists(item.PartPath);
        var dl = new SegmentedDownloader(new SegmentedDownloader.Config
        {
            Url = new Uri(item.Url),
            Headers = item.Headers,
            PartPath = item.PartPath,
            SegmentCount = item.RequestedSegments > 0 ? item.RequestedSegments : Settings.SegmentCount,
            ResumeSegments = resumable ? item.Segments : Array.Empty<SegmentInfo>(),
            ResumeTotal = resumable ? item.TotalBytes : -1,
        });
        var id = item.Id;
        var ui = Dispatcher.CurrentDispatcher;
        dl.Probed += p => ui.InvokeAsync(() => { if (_downloaders.GetValueOrDefault(id) == dl) HandleProbe(item, p); });
        _downloaders[id] = dl;
        item.State = resumable ? DownloadState.Downloading : DownloadState.Probing;
        item.ErrorMessage = null;
        item.Speed = 0;
        if (!resumable) { item.ReceivedBytes = 0; item.Segments = Array.Empty<SegmentInfo>(); }
        _ = RunFileAsync(item, dl);
    }

    async Task RunFileAsync(DownloadItem item, SegmentedDownloader dl)
    {
        Exception? error = null;
        try { await Task.Run(dl.RunAsync); }
        catch (Exception e) { error = e; }
        // Back on the UI thread.
        if (_downloaders.GetValueOrDefault(item.Id) != dl)
        {
            // Paused, cancelled or removed meanwhile. Cancel/remove leave the part file to us once the handle is closed.
            if (item.State == DownloadState.Cancelled || !Items.Contains(item)) TryDelete(item.PartPath);
            return;
        }
        _downloaders.Remove(item.Id);
        SyncProgress(item, dl);
        if (error == null) FinishFile(item);
        else if (item.State is not (DownloadState.Paused or DownloadState.Cancelled))
        {
            item.State = DownloadState.Failed;
            item.ErrorMessage = error.Message;
            item.Speed = 0;
        }
        _dirty = true;
        PumpQueue();
    }

    void HandleProbe(DownloadItem item, ProbeResult p)
    {
        item.TotalBytes = p.TotalBytes;
        item.SupportsRanges = p.SupportsRanges;
        item.State = DownloadState.Downloading;
        if (!string.IsNullOrEmpty(p.SuggestedFileName)) item.FileName = Sanitize(p.SuggestedFileName);
        else if (p.FinalUrl != null && item.FileName == Sanitize(FileNameFrom(new Uri(item.Url))) && p.FinalUrl.AbsoluteUri != item.Url)
        {
            var candidate = FileNameFrom(p.FinalUrl);
            if (Path.HasExtension(candidate)) item.FileName = Sanitize(candidate);
        }
        if (!Path.HasExtension(item.FileName) && p.ContentType != null && ExtensionForMime(p.ContentType) is string ext && ext != ".bin")
            item.FileName += ext;
    }

    void FinishFile(DownloadItem item)
    {
        try
        {
            var dir = DestinationDirectory(item);
            Directory.CreateDirectory(dir);
            var dest = UniqueDestination(Path.Combine(dir, item.FileName));
            File.SetAttributes(item.PartPath, File.GetAttributes(item.PartPath) & ~FileAttributes.Hidden);
            File.Move(item.PartPath, dest);
            MarkOfTheWeb(dest, item.Url, item.Headers.GetValueOrDefault("Referer"));
            item.FileName = Path.GetFileName(dest);
            item.FinalPath = dest;
            if (item.TotalBytes <= 0) item.TotalBytes = item.ReceivedBytes;
            Complete(item);
        }
        catch (Exception e)
        {
            item.State = DownloadState.Failed;
            item.ErrorMessage = e.Message;
        }
    }

    void Complete(DownloadItem item)
    {
        item.State = DownloadState.Completed;
        item.CompletedAt = DateTime.Now;
        item.Speed = 0;
        item.Stage = "";
        Completed?.Invoke(item);
    }

    static void SyncProgress(DownloadItem item, SegmentedDownloader dl)
    {
        item.ReceivedBytes = dl.Received;
        item.Segments = (SegmentInfo[])dl.Segments.Clone();
    }

    // MARK: - Video (yt-dlp)

    void StartVideo(DownloadItem item)
    {
        var id = item.Id;
        var url = new Uri(item.Url);
        bool missingYtDlp = VideoDownloader.LocateYtDlp(Settings.YtdlpPath) == null;
        bool missingExtras = VideoDownloader.LocateFfmpeg() == null || (VideoDownloader.IsYouTube(url) && VideoDownloader.LocateDeno() == null);
        if ((missingYtDlp || missingExtras) && !_toolsDeclined)
        {
            item.State = DownloadState.Downloading;
            item.ErrorMessage = null;
            item.Stage = S.T("Waiting for video tools…", "Video araçları bekleniyor…");
            if (!_toolWaiters.Contains(id)) _toolWaiters.Add(id);
            if (_toolWaiters.Count == 1) Dispatcher.CurrentDispatcher.InvokeAsync(PromptForTools);
            return;
        }
        if (missingYtDlp)
        {
            item.State = DownloadState.Failed;
            item.ErrorMessage = S.T("yt-dlp not found. Use “Install tools” in Settings ▸ Video.", "yt-dlp bulunamadı. Ayarlar ▸ Video ▸ “Araçları kur” seçeneğini kullanın.");
            PumpQueue();
            return;
        }

        bool useCookies = !_cookieless.Contains(id);
        item.State = DownloadState.Downloading;
        item.ErrorMessage = null;
        item.ReceivedBytes = 0;
        item.Speed = 0;
        item.TotalBytes = -1;
        item.Stage = S.T("Fetching video information…", "Video bilgisi alınıyor…");
        if (useCookies) item.Log = "";   // keep the failed attempt's output visible above the retry

        var outDir = DestinationDirectory(item);
        Directory.CreateDirectory(outDir);
        var ui = Dispatcher.CurrentDispatcher;
        _videoPhase[id] = (0, 0, -1);
        _videos[id] = VideoDownloader.Run(VideoDownloader.LocateYtDlp(Settings.YtdlpPath)!, url, outDir, Settings.YtdlpFormatArguments(),
            item.Headers, item.CookieFile, useCookies,
            onProgress: p => ui.InvokeAsync(() =>
            {
                if (!_videoPhase.TryGetValue(id, out var ph)) return;
                if (p.Downloaded < ph.Last) ph.Base += ph.Last;   // next file (audio after video) started
                ph.Last = p.Downloaded;
                _videoPhase[id] = ph;
                long received = ph.Base + p.Downloaded;
                item.ReceivedBytes = received;
                item.TotalBytes = Math.Max(Math.Max(ph.Approx, p.Total > 0 ? ph.Base + p.Total : -1), received);
                item.Speed = p.Speed;
            }),
            onLine: line => ui.InvokeAsync(() => HandleVideoLine(item, line)),
            onFinish: (path, err) => ui.InvokeAsync(() => HandleVideoFinish(item, path, err)));
    }

    void HandleVideoLine(DownloadItem item, string line)
    {
        var log = item.Log + line + "\n";
        item.Log = log.Length > 6000 ? log[^6000..] : log;
        if (VideoDownloader.Stage(line) is string st) item.Stage = st;
        if (line.StartsWith("MCFMT|"))
        {
            var p = line.Split('|');
            if (p.Length > 5 && double.TryParse(p[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
                && _videoPhase.TryGetValue(item.Id, out var ph))
            {
                _videoPhase[item.Id] = ph with { Approx = (long)n };
                if (item.TotalBytes < n) item.TotalBytes = (long)n;
            }
        }
        int d = line.IndexOf("Destination: ", StringComparison.Ordinal);
        if (d >= 0)
        {
            // Show the final title; strip yt-dlp's intermediate ".f137" format suffix.
            var name = Path.GetFileName(line[(d + 13)..]);
            item.FileName = Regex.Replace(name, @"\.f[0-9]+[-a-z0-9]*(?=\.[a-z0-9]+$)", "");
        }
        var merge = Regex.Match(line, "^\\[Merger\\] Merging formats into \"(.+)\"$");
        if (merge.Success) item.FileName = Path.GetFileName(merge.Groups[1].Value);
    }

    void HandleVideoFinish(DownloadItem item, string? path, Exception? error)
    {
        _videos.Remove(item.Id);
        _videoPhase.Remove(item.Id);
        if (item.State != DownloadState.Downloading || !Items.Contains(item)) return;   // paused/cancelled by the user
        if (error == null)
        {
            if (path != null)
            {
                item.FinalPath = path;
                item.FileName = Path.GetFileName(path);
                MarkOfTheWeb(path, item.Url, null);
                try { item.TotalBytes = item.ReceivedBytes = new FileInfo(path).Length; } catch { }
            }
            else if (item.TotalBytes > 0) item.ReceivedBytes = item.TotalBytes;
            Complete(item);
        }
        else if (!_cookieless.Contains(item.Id) && VideoDownloader.IsYouTube(new Uri(item.Url))
                 && VideoDownloader.IsSignedInClientFailure(error.Message) && item.CookieFile != null)
        {
            _cookieless.Add(item.Id);
            item.Log += S.T("→ YouTube rejected the signed-in request; retrying without cookies…", "→ YouTube oturum açılmış isteği reddetti; çerezsiz yeniden deneniyor…") + "\n";
            StartVideo(item);
            return;
        }
        else
        {
            item.State = DownloadState.Failed;
            item.ErrorMessage = error.Message;
            item.Speed = 0;
        }
        _dirty = true;
        PumpQueue();
    }

    /// Asks once whether to install yt-dlp/ffmpeg/deno, then starts the waiting downloads.
    async void PromptForTools()
    {
        if (AskInstallTools?.Invoke() != true)
        {
            _toolsDeclined = true;
            ResumeToolWaiters();
            return;
        }
        var progress = new Progress<string>(msg => { foreach (var id in _toolWaiters) if (Find(id) is { } i) i.Stage = msg; });
        try { await ToolInstaller.InstallAsync(false, progress); }
        catch (Exception e)
        {
            _toolsDeclined = true;   // don't loop on a failing install; Settings ▸ Video can retry
            foreach (var id in _toolWaiters) if (Find(id) is { } i) i.Log += S.T("Tools could not be installed: ", "Araçlar kurulamadı: ") + e.Message + "\n";
        }
        ResumeToolWaiters();
    }

    void ResumeToolWaiters()
    {
        var waiting = _toolWaiters.ToList();
        _toolWaiters.Clear();
        foreach (var id in waiting)
            if (Find(id) is { State: DownloadState.Downloading } item) StartVideo(item);
    }

    // MARK: - User actions

    void StopWork(Guid id)
    {
        if (_downloaders.Remove(id, out var dl)) { dl.Stop(); if (Find(id) is { } item) SyncProgress(item, dl); }
        if (_videos.Remove(id, out var p)) { try { p.Kill(entireProcessTree: true); } catch { } }
        _toolWaiters.Remove(id);
    }

    public void Pause(DownloadItem item)
    {
        if (!item.IsActive && item.State != DownloadState.Queued) return;
        StopWork(item.Id);
        item.State = DownloadState.Paused;
        item.Speed = 0;
        _dirty = true;
        PumpQueue();
    }

    public void Resume(DownloadItem item)
    {
        if (item.State is not (DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled)) return;
        item.State = DownloadState.Queued;
        item.ErrorMessage = null;
        _cookieless.Remove(item.Id);
        PumpQueue();
    }

    public void Cancel(DownloadItem item)
    {
        bool running = _downloaders.ContainsKey(item.Id);
        StopWork(item.Id);
        item.State = DownloadState.Cancelled;
        item.Speed = 0;
        item.ReceivedBytes = 0;
        item.Segments = Array.Empty<SegmentInfo>();
        if (!running) TryDelete(item.PartPath);   // a running downloader deletes it after closing its handle
        _dirty = true;
        PumpQueue();
    }

    public void Remove(DownloadItem item, bool deleteFile = false)
    {
        bool running = _downloaders.ContainsKey(item.Id);
        StopWork(item.Id);
        if (!running) TryDelete(item.PartPath);
        if (item.CookieFile != null) TryDelete(item.CookieFile);
        if (deleteFile && item.FinalPath is string fp && File.Exists(fp))
        {
            // Recycle Bin, so the user can recover the file.
            try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(fp, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); } catch { }
        }
        Items.Remove(item);
        _samples.Remove(item.Id);
        _dirty = true;
        PumpQueue();
    }

    public void PauseAll() { foreach (var i in Items.Where(i => i.IsActive || i.State == DownloadState.Queued).ToList()) Pause(i); }
    public void ResumeAll() { foreach (var i in Items.Where(i => i.State is DownloadState.Paused or DownloadState.Failed).ToList()) Resume(i); }
    public void ClearFinished() { foreach (var i in Items.Where(i => i.State is DownloadState.Completed or DownloadState.Cancelled).ToList()) Remove(i); }

    public void ShowInFolder(DownloadItem item)
    {
        if (item.FinalPath is string fp && File.Exists(fp)) Process.Start("explorer.exe", $"/select,\"{fp}\"");
        else
        {
            var dir = DestinationDirectory(item);
            Process.Start("explorer.exe", $"\"{(Directory.Exists(dir) ? dir : item.Directory)}\"");
        }
    }

    public void Open(DownloadItem item)
    {
        if (item.FinalPath is string fp && File.Exists(fp)) Process.Start(new ProcessStartInfo(fp) { UseShellExecute = true });
    }

    public void Shutdown()
    {
        PauseAll();
        Save();
    }

    // MARK: - Speed + periodic save

    void Tick()
    {
        var now = DateTime.UtcNow;
        var active = new HashSet<Guid>();
        foreach (var item in Items)
        {
            if (_downloaders.TryGetValue(item.Id, out var dl)) SyncProgress(item, dl);
            if (item.State != DownloadState.Downloading || item.Kind == DownloadKind.Video) continue;
            active.Add(item.Id);
            if (_samples.TryGetValue(item.Id, out var s))
            {
                double dt = (now - s.Time).TotalSeconds;
                if (dt > 0)
                {
                    double inst = Math.Max(0, (item.ReceivedBytes - s.Bytes) / dt);
                    item.Speed = item.Speed == 0 ? inst : item.Speed * 0.6 + inst * 0.4;
                }
            }
            _samples[item.Id] = (item.ReceivedBytes, now);
        }
        foreach (var k in _samples.Keys.Where(k => !active.Contains(k)).ToList()) _samples.Remove(k);
        if (_dirty || (active.Count > 0 && ++_tick % 10 == 0)) Save();   // segment state to disk every 5 s
    }

    // MARK: - Persistence

    void Load()
    {
        try
        {
            var arr = JsonSerializer.Deserialize<List<DownloadItem>>(File.ReadAllText(StorePath)) ?? new();
            foreach (var i in arr)
            {
                if (i.IsActive || i.State == DownloadState.Queued) i.State = DownloadState.Paused;
                Items.Add(i);
            }
        }
        catch { }
        CleanupOrphanParts();
    }

    /// Deletes `.dm-<id>.part` files left over from downloads that are no longer in the list.
    void CleanupOrphanParts()
    {
        try
        {
            var known = Items.Select(i => $".dm-{i.Id}.part").ToHashSet();
            foreach (var f in Directory.EnumerateFiles(Settings.DownloadDirectory, ".dm-*.part"))
                if (!known.Contains(Path.GetFileName(f))) TryDelete(f);
        }
        catch { }
    }

    void Save()
    {
        _dirty = false;
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Items));
            File.Move(tmp, StorePath, overwrite: true);
        }
        catch { }
    }

    static string? WriteCookieFile(List<JsonElement> cookies, Guid id)
    {
        var lines = new List<string> { "# Netscape HTTP Cookie File" };
        foreach (var c in cookies)
        {
            if (c.ValueKind != JsonValueKind.Object) continue;
            string? Str(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (Str("name") is not string name || Str("value") is not string value || Str("domain") is not string domain) continue;
            bool secure = c.TryGetProperty("secure", out var s) && s.ValueKind == JsonValueKind.True;
            long exp = c.TryGetProperty("expirationDate", out var e) && e.ValueKind == JsonValueKind.Number ? (long)e.GetDouble() : 0;
            lines.Add(string.Join('\t', domain, domain.StartsWith('.') ? "TRUE" : "FALSE", Str("path") ?? "/", secure ? "TRUE" : "FALSE", exp, name, value));
        }
        try
        {
            var dir = Path.Combine(AppSettings.DataDir, "cookies");   // %LOCALAPPDATA% is private to the user by default ACL
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{id}.txt");
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            return path;
        }
        catch { return null; }
    }

    // MARK: - File helpers

    /// Tags a downloaded file like a browser download, so SmartScreen/Office treat it as coming from the internet.
    static void MarkOfTheWeb(string file, string url, string? referer)
    {
        try
        {
            var body = "[ZoneTransfer]\r\nZoneId=3\r\n" + (referer != null ? $"ReferrerUrl={referer}\r\n" : "") + $"HostUrl={url}\r\n";
            File.WriteAllText(file + ":Zone.Identifier", body);
        }
        catch { }
    }

    static string? ExtensionForMime(string mime)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"MIME\Database\Content Type\{mime}");
        return key?.GetValue("Extension") as string;
    }

    static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    public static string FileNameFrom(Uri url)
    {
        var name = Uri.UnescapeDataString(url.Segments.LastOrDefault() ?? "").Trim('/');
        return string.IsNullOrEmpty(name) ? (string.IsNullOrEmpty(url.Host) ? "download" : url.Host) : name;
    }

    static readonly Regex Reserved = new(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$", RegexOptions.IgnoreCase);

    public static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var n = new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (n.StartsWith('.')) n = "_" + n;
        if (Reserved.IsMatch(n)) n = "_" + n;
        if (n.Length > 200)
        {
            var ext = Path.GetExtension(n);
            n = Path.GetFileNameWithoutExtension(n)[..180] + (ext.Length <= 16 ? ext : "");
        }
        return n.Length == 0 ? "download" : n;
    }

    public static string UniqueDestination(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var ext = Path.GetExtension(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        for (int n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
