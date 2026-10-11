using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MCDM;

/// yt-dlp wrapper. Tools live in %LOCALAPPDATA%\MC Download Manager\bin (installed by ToolInstaller), else PATH.
public static class VideoDownloader
{
    public static string BinDir => Path.Combine(AppSettings.DataDir, "bin");

    static string? Locate(string exe, string? custom = null)
    {
        var candidates = new List<string?> { custom, Path.Combine(BinDir, exe), Path.Combine(AppContext.BaseDirectory, "tools", exe) };
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim('"'), exe)));
        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
    }

    public static string? LocateYtDlp(string custom) => Locate("yt-dlp.exe", custom);
    public static string? LocateFfmpeg() => Locate("ffmpeg.exe");
    /// deno is the JavaScript runtime yt-dlp uses to solve YouTube's n/signature challenges.
    public static string? LocateDeno() => Locate("deno.exe");

    public static bool IsYouTube(Uri url)
    {
        var host = url.Host.ToLowerInvariant();
        return new[] { "youtube.com", "youtu.be", "youtube-nocookie.com" }.Any(d => host == d || host.EndsWith("." + d));
    }

    /// YouTube rejects the signed-in clients yt-dlp picks when cookies are passed (yt-dlp#17389);
    /// the same video usually works without cookies.
    public static bool IsSignedInClientFailure(string message) =>
        message.Contains("page needs to be reloaded", StringComparison.OrdinalIgnoreCase);

    public readonly record struct Progress(long Downloaded, long Total, double Speed);

    /// Turns a yt-dlp log line into a user-facing phase description, if it marks a new phase.
    public static string? Stage(string line)
    {
        if (line.StartsWith("[download] Destination:"))
        {
            var lower = line.ToLowerInvariant();
            // yt-dlp names intermediate files <title>.f<formatid>.<ext>
            if (Regex.IsMatch(lower, @"\.f[0-9]+[-a-z0-9]*\.(m4a|webm|opus|mp3|aac|ogg)$")) return S.T("Downloading audio…", "Ses indiriliyor…");
            if (Regex.IsMatch(lower, @"\.f[0-9]+[-a-z0-9]*\.[a-z0-9]+$")) return S.T("Downloading video…", "Video indiriliyor…");
            return S.T("Downloading…", "İndiriliyor…");
        }
        if (line.StartsWith("[Merger]")) return S.T("Merging video and audio (ffmpeg)…", "Video ve ses birleştiriliyor (ffmpeg)…");
        if (line.StartsWith("[VideoRemuxer]") || line.StartsWith("[VideoConvertor]")) return S.T("Converting container (ffmpeg)…", "Kapsayıcı dönüştürülüyor (ffmpeg)…");
        if (line.StartsWith("[ExtractAudio]")) return S.T("Extracting audio (ffmpeg)…", "Ses ayıklanıyor (ffmpeg)…");
        if (line.StartsWith("MCFMT|"))
        {
            var p = line.Split('|');
            if (p.Length < 6) return null;
            var parts = new List<string>();
            if (p[1] != "NA" && p[1] != "") parts.Add(p[1]);
            if (p[2] != "NA") parts.Add(p[2].ToUpperInvariant());
            if (p[3] != "NA" && p[3] != "none") parts.Add(p[3].Split('.')[0]);
            if (double.TryParse(p[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0)
                parts.Add("~" + Fmt.Size((long)n));
            return S.T("Selected: ", "Seçilen: ") + string.Join(" · ", parts);
        }
        if (line.Contains("has already been downloaded")) return S.T("File already downloaded", "Dosya zaten indirilmiş");
        return null;
    }

    /// Starts yt-dlp. Callbacks run on thread-pool threads; onFinish gets the final file path (may be null) or an error.
    public static Process Run(string exe, Uri url, string outputDir, List<string> formatArgs, Dictionary<string, string> headers,
        string? cookieFile, bool useCookies, Action<Progress> onProgress, Action<string> onLine, Action<string?, Exception?> onFinish)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = outputDir,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var a in new[] {
            "--newline", "--no-playlist", "--no-colors", "--progress", "--no-simulate", "--no-mtime", "--no-quiet", "--encoding", "utf-8",
            "-N", "4",
            "-o", Path.Combine(outputDir, "%(title).150B [%(id)s].%(ext)s"),
            "--progress-template",
            "download:DMP|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s",
            "--print", "after_move:filepath",
            "--print", "before_dl:MCFMT|%(resolution)s|%(ext)s|%(vcodec)s|%(acodec)s|%(filesize_approx)s|%(format_id)s|%(duration)s",
        }) psi.ArgumentList.Add(a);
        formatArgs.ForEach(psi.ArgumentList.Add);
        void Arg(string k, string? v) { if (!string.IsNullOrEmpty(v)) { psi.ArgumentList.Add(k); psi.ArgumentList.Add(v); } }
        Arg("--ffmpeg-location", LocateFfmpeg());
        Arg("--user-agent", headers.GetValueOrDefault("User-Agent"));
        Arg("--referer", headers.GetValueOrDefault("Referer"));
        if (useCookies && cookieFile != null && File.Exists(cookieFile)) Arg("--cookies", cookieFile);
        if (LocateDeno() is string deno) Arg("--js-runtimes", "deno:" + deno);
        psi.ArgumentList.Add(url.AbsoluteUri);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        string? finalPath = null, lastError = null;
        object gate = new();

        void Handle(string? raw, bool isError)
        {
            var line = raw?.Trim();
            if (string.IsNullOrEmpty(line)) return;
            if (line.StartsWith("DMP|"))
            {
                var parts = line.Split('|');
                if (parts.Length < 5) return;
                static long Num(string s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? (long)d : -1;
                long total = Num(parts[2]);
                if (total <= 0) total = Num(parts[3]);
                onProgress(new Progress(Math.Max(0, Num(parts[1])), total, Math.Max(0, Num(parts[4]))));
                return;
            }
            lock (gate)
            {
                if (Path.IsPathFullyQualified(line) && File.Exists(line)) finalPath = line;
                if (isError && line.StartsWith("ERROR")) lastError = line;
            }
            onLine(line);
        }
        p.OutputDataReceived += (_, e) => Handle(e.Data, false);
        p.ErrorDataReceived += (_, e) => Handle(e.Data, true);

        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch (Exception e)
        {
            onFinish(null, e);
            return p;
        }
        _ = Task.Run(async () =>
        {
            await p.WaitForExitAsync();   // also waits for the redirected streams to reach EOF
            lock (gate)
            {
                if (p.ExitCode == 0) onFinish(finalPath, null);
                else onFinish(null, new DownloadFailure(lastError ?? S.T($"yt-dlp exited with code {p.ExitCode}", $"yt-dlp {p.ExitCode} koduyla çıktı")));
            }
        });
        return p;
    }
}

/// Installs yt-dlp, ffmpeg and deno from their official GitHub releases into BinDir, verifying SHA-256 checksums.
public static class ToolInstaller
{
    const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    const string YtDlpSums = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    const string FfmpegZip = "ffmpeg-master-latest-win64-gpl.zip";
    const string FfmpegBase = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/";
    const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    static ToolInstaller() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("MCDownloadManager");

    public static bool Busy { get; private set; }

    /// Installs whatever is missing (or everything when force is set). progress gets a status line.
    public static async Task InstallAsync(bool force, IProgress<string> progress)
    {
        if (Busy) throw new DownloadFailure(S.T("Another tool install is in progress.", "Başka bir araç kurulumu sürüyor."));
        Busy = true;
        try
        {
            Directory.CreateDirectory(VideoDownloader.BinDir);
            string bin = VideoDownloader.BinDir;
            if (force || VideoDownloader.LocateYtDlp("") == null)
            {
                var tmp = Path.Combine(bin, "new-yt-dlp.exe");
                await DownloadAsync(YtDlpUrl, tmp, "yt-dlp", progress);
                await VerifyAsync(tmp, YtDlpSums, "yt-dlp.exe");
                Replace(tmp, Path.Combine(bin, "yt-dlp.exe"));
            }
            if (force || VideoDownloader.LocateFfmpeg() == null)
            {
                var zip = Path.Combine(bin, "ffmpeg.zip");
                await DownloadAsync(FfmpegBase + FfmpegZip, zip, "ffmpeg", progress);
                await VerifyAsync(zip, FfmpegBase + "checksums.sha256", FfmpegZip);
                Extract(zip, bin, "ffmpeg.exe", "ffprobe.exe");
            }
            if (force || VideoDownloader.LocateDeno() == null)
            {
                var zip = Path.Combine(bin, "deno.zip");
                await DownloadAsync(DenoUrl, zip, "deno", progress);
                await VerifyAsync(zip, DenoUrl + ".sha256sum", null);
                Extract(zip, bin, "deno.exe");
            }
            progress.Report(S.T("Tools installed: ", "Araçlar kuruldu: ") + (Version(Path.Combine(bin, "yt-dlp.exe")) ?? "yt-dlp"));
        }
        finally { Busy = false; }
    }

    static async Task DownloadAsync(string url, string dest, string label, IProgress<string> progress)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(dest);
        var buf = new byte[81920];
        int n, lastPct = -1;
        while ((n = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n));
            done += n;
            int pct = total > 0 ? (int)(done * 100 / total) : -1;
            if (pct != lastPct) { lastPct = pct; progress.Report(S.T($"Downloading {label}… ", $"{label} indiriliyor… ") + (pct >= 0 ? $"{pct}%" : Fmt.Size(done))); }
        }
    }

    /// Compares the file's SHA-256 with the hash published next to the release (line for `name`, or the only hash).
    static async Task VerifyAsync(string file, string sumsUrl, string? name)
    {
        string sums = await Http.GetStringAsync(sumsUrl);
        var line = name == null ? sums : sums.Split('\n').FirstOrDefault(l => l.TrimEnd().EndsWith(name, StringComparison.OrdinalIgnoreCase)) ?? "";
        var expected = Regex.Match(line, "[0-9a-fA-F]{64}").Value.ToLowerInvariant();
        string actual;
        await using (var fs = File.OpenRead(file)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
        if (expected.Length != 64 || actual != expected)
        {
            File.Delete(file);
            throw new DownloadFailure(S.T("Checksum mismatch; the download was discarded.", "Sağlama toplamı uyuşmadı; indirilen dosya silindi."));
        }
    }

    static void Extract(string zip, string dir, params string[] names)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            foreach (var name in names)
            {
                var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new DownloadFailure(S.T($"{name} not found in the archive.", $"{name} arşivde bulunamadı."));
                var tmp = Path.Combine(dir, "new-" + name);
                entry.ExtractToFile(tmp, overwrite: true);
                Replace(tmp, Path.Combine(dir, name));
            }
        }
        finally { File.Delete(zip); }
    }

    static void Replace(string tmp, string dest)
    {
        if (Version(tmp) == null) { File.Delete(tmp); throw new DownloadFailure(S.T("The downloaded tool could not be executed.", "İndirilen araç çalıştırılamadı.")); }
        File.Move(tmp, dest, overwrite: true);
    }

    /// First line of `<tool> --version` (ffmpeg uses -version).
    public static string? Version(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            bool ff = Path.GetFileName(path).Contains("ff", StringComparison.OrdinalIgnoreCase);
            var p = Process.Start(new ProcessStartInfo(path, ff ? "-version" : "--version")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            var first = p.StandardOutput.ReadLine();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
        }
        catch { return null; }
    }
}
