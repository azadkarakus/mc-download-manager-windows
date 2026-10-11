using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCDM;

public enum DownloadKind { File, Video }

public enum DownloadState { Queued, Probing, Downloading, Paused, Completed, Failed, Cancelled }

public enum FileCategory { Image, Music, Video, Apps, Document, Compressed, Other }

public struct SegmentInfo
{
    public long Start { get; set; }
    public long End { get; set; }       // inclusive
    public long Received { get; set; }

    [JsonIgnore] public long Length => End - Start + 1;
    [JsonIgnore] public bool IsDone => Received >= Length;
}

public sealed class DownloadItem : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get => _url; set => Set(ref _url, value); }
    public string FileName { get => _fileName; set => Set(ref _fileName, value); }
    public string Directory { get; set; } = "";
    public DownloadKind Kind { get; set; }
    public DownloadState State { get => _state; set => Set(ref _state, value); }
    public long TotalBytes { get => _total; set => Set(ref _total, value); }
    public long ReceivedBytes { get => _received; set => Set(ref _received, value); }
    public bool SupportsRanges { get; set; }
    public SegmentInfo[] Segments { get => _segments; set => Set(ref _segments, value); }
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? CookieFile { get; set; }
    public string? ErrorMessage { get => _error; set => Set(ref _error, value); }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
    public string? FinalPath { get; set; }
    public int RequestedSegments { get; set; }   // 0 = value from settings

    // Not persisted
    [JsonIgnore] public double Speed { get => _speed; set => Set(ref _speed, value); }
    [JsonIgnore] public string Log { get => _log; set => Set(ref _log, value); }
    [JsonIgnore] public string Stage { get => _stage; set => Set(ref _stage, value); }

    /// Part file is id-based so it stays stable when the name changes. Hidden attribute set on create.
    [JsonIgnore] public string PartPath => Path.Combine(Directory, $".dm-{Id}.part");
    [JsonIgnore] public double Progress => TotalBytes > 0 ? Math.Min(1, (double)ReceivedBytes / TotalBytes) : 0;
    [JsonIgnore] public double? Eta => TotalBytes > 0 && Speed > 1 ? (TotalBytes - ReceivedBytes) / Speed : null;
    [JsonIgnore] public FileCategory Category => Categories.Detect(FileName, Kind);
    [JsonIgnore] public bool IsActive => State is DownloadState.Probing or DownloadState.Downloading;

    // Display helpers for bindings
    [JsonIgnore] public string SizeText => TotalBytes > 0 ? Fmt.Size(TotalBytes) : (ReceivedBytes > 0 ? Fmt.Size(ReceivedBytes) : "—");
    [JsonIgnore] public string Glyph => Categories.Glyph(Category);
    /// Second line under the name: category, or the live phase while downloading.
    [JsonIgnore] public string Subtitle => State == DownloadState.Downloading && Stage.Length > 0 ? Stage : S.CategoryLabel(Category);
    [JsonIgnore] public bool ShowProgress => State is not (DownloadState.Completed or DownloadState.Cancelled or DownloadState.Failed);
    [JsonIgnore] public string PercentText => TotalBytes > 0 && ShowProgress ? $"{(int)(Progress * 100)}%" : "";
    [JsonIgnore] public string SpeedText => State == DownloadState.Downloading && Speed > 0 ? Fmt.Speed(Speed) : "";
    [JsonIgnore] public string StatusText => State switch
    {
        DownloadState.Downloading when Eta is double e => $"{(int)(Progress * 100)}% · {Fmt.Duration(e)}",
        DownloadState.Downloading when TotalBytes > 0 => $"{(int)(Progress * 100)}%",
        DownloadState.Failed => S.StateLabel(State) + (ErrorMessage is null ? "" : ": " + ErrorMessage),
        _ => S.StateLabel(State),
    };

    string _url = "", _fileName = "", _log = "", _stage = "";
    DownloadState _state;
    long _total = -1, _received;
    SegmentInfo[] _segments = Array.Empty<SegmentInfo>();
    string? _error;
    double _speed;

    public event PropertyChangedEventHandler? PropertyChanged;
    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));   // for live filtering
        // ponytail: also refresh every binding of the item (computed text depends on several fields); per-property notify if lists get huge
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

public static class Categories
{
    static readonly Dictionary<FileCategory, string[]> Table = new()
    {
        [FileCategory.Image] = new[] { "jpg", "jpeg", "png", "gif", "webp", "heic", "heif", "bmp", "tiff", "tif", "svg", "psd", "ai", "raw", "cr2", "nef", "ico" },
        [FileCategory.Music] = new[] { "mp3", "m4a", "aac", "flac", "wav", "ogg", "opus", "wma", "aiff", "alac" },
        [FileCategory.Video] = new[] { "mp4", "m4v", "mkv", "webm", "mov", "avi", "flv", "wmv", "mpg", "mpeg", "ts", "m3u8", "3gp" },
        [FileCategory.Apps] = new[] { "dmg", "pkg", "app", "exe", "msi", "msix", "appx", "apk", "ipa", "deb", "rpm", "appimage", "jar", "xpi", "crx" },
        [FileCategory.Document] = new[] { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "odt", "ods", "odp", "epub", "mobi", "csv", "md" },
        [FileCategory.Compressed] = new[] { "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "iso", "img", "cab" },
    };

    /// Segoe Fluent Icons glyph per category.
    public static string Glyph(FileCategory c) => c switch
    {
        FileCategory.Image => "",
        FileCategory.Music => "",
        FileCategory.Video => "",
        FileCategory.Apps => "",
        FileCategory.Document => "",
        FileCategory.Compressed => "",
        _ => "",
    };

    public static FileCategory Detect(string fileName, DownloadKind kind)
    {
        if (kind == DownloadKind.Video) return FileCategory.Video;
        var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        foreach (var (cat, exts) in Table) if (exts.Contains(ext)) return cat;
        return FileCategory.Other;
    }

    /// File types the app captures (browser downloads, clipboard links), grouped for the settings UI.
    public static readonly (string Id, FileCategory Category, string[] Extensions)[] CaptureGroups =
    {
        ("compressed", FileCategory.Compressed, new[] { "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "iso", "img", "cab" }),
        ("apps", FileCategory.Apps, new[] { "exe", "msi", "msix", "dmg", "pkg", "apk", "ipa", "deb", "rpm", "appimage", "jar", "bin" }),
        ("video", FileCategory.Video, new[] { "mp4", "m4v", "mkv", "webm", "mov", "avi", "flv", "wmv", "mpg", "mpeg", "m3u8", "3gp" }),
        ("music", FileCategory.Music, new[] { "mp3", "m4a", "aac", "flac", "wav", "ogg", "opus", "wma", "aiff" }),
        ("document", FileCategory.Document, new[] { "pdf", "epub", "mobi", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf", "csv" }),
        ("image", FileCategory.Image, new[] { "jpg", "jpeg", "png", "gif", "webp", "heic", "bmp", "tiff", "psd", "svg", "raw" }),
    };

    public static List<string> CaptureExtensions(IEnumerable<string> groupIds, string custom)
    {
        var ids = groupIds.ToHashSet();
        var outList = CaptureGroups.Where(g => ids.Contains(g.Id)).SelectMany(g => g.Extensions).ToList();
        foreach (var token in custom.ToLowerInvariant().Split(new[] { ',', ' ', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var e = token.Trim('.', ' ');
            if (e.Length > 0 && !outList.Contains(e)) outList.Add(e);
        }
        return outList;
    }

    public static bool Matches(Uri url, List<string> extensions)
    {
        var ext = Path.GetExtension(url.AbsolutePath).TrimStart('.').ToLowerInvariant();
        return ext.Length > 0 && extensions.Contains(ext);
    }
}

public sealed class AppSettings
{
    public int SegmentCount { get; set; } = 8;
    public int MaxConcurrent { get; set; } = 3;
    public int ServerPort { get; set; } = 48213;
    public string DownloadDirectory { get; set; } = DefaultDownloadDir;
    public string YtdlpPath { get; set; } = "";
    public string VideoFormat { get; set; } = "";       // custom -f (overrides pickers when non-empty)
    public string VideoContainer { get; set; } = "mp4"; // mp4 | mkv | webm | original
    public int VideoMaxHeight { get; set; } = 1080;     // 0 = best, -1 = audio m4a, -2 = audio mp3
    public bool AutoStart { get; set; } = true;
    public bool NotifyOnComplete { get; set; } = true;
    public bool ShowDetailOnStart { get; set; } = true;
    public bool AutoCloseDetail { get; set; }
    public bool WatchClipboard { get; set; } = true;
    public List<string> CaptureGroupIds { get; set; } = new() { "compressed", "apps", "video", "music", "document" };
    public string CustomExtensions { get; set; } = "";
    public bool OnboardingDone { get; set; }
    public bool OrganizeByCategory { get; set; } = true;

    [JsonIgnore] public List<string> CaptureExtensions => Categories.CaptureExtensions(CaptureGroupIds, CustomExtensions);

    public static string DefaultDownloadDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MC Download Manager");

    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MC Download Manager");
    static string FilePath => Path.Combine(DataDir, "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// yt-dlp arguments for the chosen format/quality.
    public List<string> YtdlpFormatArguments()
    {
        var custom = VideoFormat.Trim();
        if (custom.Length > 0) return new() { "-f", custom, "--merge-output-format", VideoContainer == "original" ? "mkv" : VideoContainer };
        if (VideoMaxHeight == -1) return new() { "-f", "ba/b", "-x", "--audio-format", "m4a", "--audio-quality", "0" };
        if (VideoMaxHeight == -2) return new() { "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0" };
        var h = VideoMaxHeight > 0 ? $"[height<={VideoMaxHeight}]" : "";
        return VideoContainer switch
        {
            // Prefer H.264 + AAC so the file plays everywhere; fall back to anything and merge into mp4.
            "mp4" => new() { "-f", $"bv*{h}[vcodec^=avc1]+ba[acodec^=mp4a]/bv*{h}[ext=mp4]+ba[ext=m4a]/bv*{h}+ba/b{h}/b", "--merge-output-format", "mp4", "--remux-video", "mp4" },
            "webm" => new() { "-f", $"bv*{h}[ext=webm]+ba[ext=webm]/bv*{h}+ba/b{h}/b", "--merge-output-format", "webm" },
            "mkv" => new() { "-f", $"bv*{h}+ba/b{h}/b", "--merge-output-format", "mkv" },
            _ => new() { "-f", $"bv*{h}+ba/b{h}/b" },
        };
    }
}

public static class Fmt
{
    public static string Size(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {u[i]}";
    }
    public static string Speed(double bps) => Size((long)bps) + "/s";
    public static string Duration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}

/// UI strings: English, or Turkish when the Windows display language is Turkish.
public static class S
{
    public static readonly bool Tr = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";
    public static string T(string en, string tr) => Tr ? tr : en;

    public static string StateLabel(DownloadState s) => s switch
    {
        DownloadState.Queued => T("Queued", "Sırada"),
        DownloadState.Probing => T("Connecting", "Bağlanıyor"),
        DownloadState.Downloading => T("Downloading", "İndiriliyor"),
        DownloadState.Paused => T("Paused", "Duraklatıldı"),
        DownloadState.Completed => T("Completed", "Tamamlandı"),
        DownloadState.Failed => T("Error", "Hata"),
        _ => T("Cancelled", "İptal edildi"),
    };

    public static string CategoryLabel(FileCategory c) => c switch
    {
        FileCategory.Image => T("Images", "Resimler"),
        FileCategory.Music => T("Music", "Müzik"),
        FileCategory.Video => T("Video", "Video"),
        FileCategory.Apps => T("Apps", "Uygulamalar"),
        FileCategory.Document => T("Documents", "Belgeler"),
        FileCategory.Compressed => T("Archives", "Arşivler"),
        _ => T("Other", "Diğer"),
    };

    // Bound from XAML via {x:Static}
    public static string AddDownload => T("New Download", "Yeni İndirme");
    public static string Resume => T("Resume", "Devam");
    public static string Pause => T("Pause", "Duraklat");
    public static string Cancel => T("Cancel", "İptal");
    public static string Remove => T("Remove", "Kaldır");
    public static string RemoveAndDelete => T("Remove and Delete File", "Kaldır ve Dosyayı Sil");
    public static string PauseAll => T("Pause All", "Tümünü Duraklat");
    public static string ResumeAll => T("Resume All", "Tümünü Devam Ettir");
    public static string ClearFinished => T("Clear Finished", "Bitenleri Temizle");
    public static string Settings => T("Settings", "Ayarlar");
    public static string Open => T("Open", "Aç");
    public static string ShowInFolder => T("Show in Folder", "Klasörde Göster");
    public static string CopyLink => T("Copy Link", "Bağlantıyı Kopyala");
    public static string Details => T("Details", "Ayrıntılar");
    public static string Search => T("Search", "Ara");
    public static string SearchHint => T("Search in the list", "Listede ara");
    public static string EmptyTitle => T("No downloads", "İndirme yok");
    public static string EmptyHint => T("Downloads from your browser appear here.\nPress Ctrl+V to paste a link or Ctrl+N to add one.",
        "Tarayıcınızdan gelen indirmeler burada görünür.\nBağlantı yapıştırmak için Ctrl+V, eklemek için Ctrl+N.");
    public static string ColName => T("Name", "Ad");
    public static string ColSize => T("Size", "Boyut");
    public static string ColProgress => T("Progress", "İlerleme");
    public static string ColSpeed => T("Speed", "Hız");
    public static string ColStatus => T("Status", "Durum");
    public static string ColDate => T("Added", "Eklenme");
    public static string Download => T("Download", "İndir");
    public static string DownloadVideo => T("Download Video (yt-dlp)", "Video İndir (yt-dlp)");
    public static string Close => T("Close", "Kapat");
    public static string Ok => T("OK", "Tamam");
    public static string AutoCloseWhenDone => T("Close this window when the download completes", "İndirme bitince bu pencereyi kapat");
    public static string PasteLinks => T("Links (one per line):", "Bağlantılar (her satıra bir tane):");
    public static string Connections => T("Connections:", "Bağlantı sayısı:");
}
