using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace MCDM;

public partial class App : Application
{
    public static AppSettings Settings = null!;
    public static DownloadStore Store = null!;
    public static BridgeServer Bridge = null!;

    Mutex? _mutex;
    Forms.NotifyIcon? _tray;
    MainWindow? _main;
    string? _lastCompletedPath;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance: a second launch just brings up the running app's window.
        _mutex = new Mutex(true, "MCDownloadManager.SingleInstance", out bool first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting("MCDownloadManager.Show").Set(); } catch { }
            Shutdown();
            return;
        }
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "MCDownloadManager.Show");
        new Thread(() => { while (showEvent.WaitOne()) Dispatcher.Invoke(ShowMain); }) { IsBackground = true }.Start();

        Settings = AppSettings.Load();
        Store = new DownloadStore(Settings);
        Bridge = new BridgeServer(Store);
        Bridge.Start(Settings.ServerPort);

        Store.Added = item => { if (Settings.ShowDetailOnStart) DetailWindow.ShowFor(item); };
        Store.Completed = OnCompleted;
        Store.AskInstallTools = () => Dialogs.Ask(
            S.T("Install the video tools?", "Video araçları kurulsun mu?"),
            S.T("Video downloads need yt-dlp, ffmpeg and (for YouTube) deno. MC Download Manager can download them from their official GitHub releases (~150 MB), verify their checksums and keep them in its own folder.",
                "Video indirmek için yt-dlp, ffmpeg ve (YouTube için) deno gerekir. MC Download Manager bunları resmi GitHub sürümlerinden indirip (~150 MB) sağlama toplamlarını doğrular ve kendi klasöründe tutar."),
            new[] { S.T("Install", "Kur"), S.T("Not Now", "Şimdi Değil") }).Button == 0;

        CopyExtension();
        SetupTray();
        StartClipboardWatcher();
        new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateTray(), Dispatcher);

        // Launched at login → stay in the tray. Launched by the user → show the window.
        if (!e.Args.Contains("--hidden")) ShowMain();
        if (!Settings.OnboardingDone)
        {
            Settings.OnboardingDone = true;
            Directory.CreateDirectory(Settings.DownloadDirectory);
            var r = Dialogs.Ask(S.T("Start with Windows?", "Windows ile başlatılsın mı?"),
                S.T("MC Download Manager can start in the system tray when you sign in, so browser downloads are captured without opening the app. You can change this in Settings.",
                    "MC Download Manager oturum açtığınızda sistem tepsisinde başlayabilir; böylece uygulamayı açmadan tarayıcı indirmeleri yakalanır. Bunu Ayarlar'dan değiştirebilirsiniz."),
                new[] { S.T("Start with Windows", "Windows ile başlat"), S.T("No", "Hayır") });
            StartWithWindows = r.Button == 0;
            Settings.Save();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Store?.Shutdown();
        _tray?.Dispose();
        base.OnExit(e);
    }

    public void ShowMain()
    {
        _main ??= new MainWindow();
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public static void Quit() => Current.Shutdown();

    // MARK: - Tray

    void SetupTray()
    {
        using var icon = GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream;
        // WPF menu (not ContextMenuStrip) so the tray menu gets the same Fluent look as the windows.
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var status = new MenuItem { IsEnabled = false };
        void Item(string glyph, string text, Action click)
        {
            var mi = new MenuItem { Header = text, Icon = new TextBlock { Text = glyph, FontFamily = (System.Windows.Media.FontFamily)FindResource("Icons") } };
            mi.Click += (_, _) => click();
            menu.Items.Add(mi);
        }
        menu.Items.Add(status);
        menu.Items.Add(new Separator());
        Item("", S.T("Show Window", "Pencereyi Göster"), ShowMain);
        Item("", S.AddDownload + "…", () => { ShowMain(); _main!.ShowAddDialog(); });
        menu.Items.Add(new Separator());
        Item("", S.PauseAll, Store.PauseAll);
        Item("", S.ResumeAll, Store.ResumeAll);
        menu.Items.Add(new Separator());
        Item("", S.Settings + "…", SettingsWindow.ShowSingle);
        Item("", S.T("Quit MC Download Manager", "MC Download Manager'dan Çık"), Quit);

        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(icon),
            Text = "MC Download Manager",
            Visible = true,
        };
        _tray.MouseUp += (_, a) =>
        {
            if (a.Button == Forms.MouseButtons.Left) { ShowMain(); return; }
            if (a.Button != Forms.MouseButtons.Right) return;
            status.Header = Store.ActiveCount > 0
                ? S.T($"{Store.ActiveCount} downloading · {Fmt.Speed(Store.TotalSpeed)}", $"{Store.ActiveCount} indiriliyor · {Fmt.Speed(Store.TotalSpeed)}")
                : S.T("No active downloads", "Etkin indirme yok");
            menu.IsOpen = true;
            // The popup must own the foreground, or it won't close when the user clicks elsewhere.
            if (PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource src) SetForegroundWindow(src.Handle);
        };
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_lastCompletedPath != null && File.Exists(_lastCompletedPath))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_lastCompletedPath}\"");
        };
    }

    void UpdateTray()
    {
        if (_tray == null) return;
        var text = "MC Download Manager";
        if (Store.ActiveCount > 0)
            text += $"\n{Store.ActiveCount} · {(Store.OverallProgress is double p ? $"{(int)(p * 100)}% · " : "")}{Fmt.Speed(Store.TotalSpeed)}";
        _tray.Text = text;
    }

    void OnCompleted(DownloadItem item)
    {
        if (Settings.NotifyOnComplete && _tray != null)
        {
            _lastCompletedPath = item.FinalPath;
            _tray.ShowBalloonTip(5000, S.T("Download completed", "İndirme tamamlandı"), item.FileName, Forms.ToolTipIcon.Info);
        }
        DetailWindow.DidComplete(item, Settings.AutoCloseDetail);
    }

    // MARK: - Start with Windows

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartWithWindows
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("MC Download Manager") != null; }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("MC Download Manager", $"\"{Environment.ProcessPath}\" --hidden");
            else k.DeleteValue("MC Download Manager", false);
        }
    }

    // MARK: - Browser extension

    /// Stable folder for Chrome/Edge "Load unpacked" (survives moving the exe; refreshed on every start).
    public static string ExtensionDir => Path.Combine(AppSettings.DataDir, "BrowserExtension");

    static void CopyExtension()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "Extension");
        if (!Directory.Exists(src)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(ExtensionDir, Path.GetRelativePath(src, f));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(f, dest, overwrite: true);
            }
        }
        catch { }
    }

    // MARK: - Clipboard

    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    static readonly string[] VideoHosts = { "youtube.com", "youtu.be", "vimeo.com", "dailymotion.com", "twitter.com", "x.com",
        "instagram.com", "tiktok.com", "twitch.tv", "facebook.com", "reddit.com", "bilibili.com" };

    uint _clipSeq;
    readonly List<string> _askedUrls = new();
    bool _prompting;

    void StartClipboardWatcher()
    {
        _clipSeq = GetClipboardSequenceNumber();
        new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => PollClipboard(), Dispatcher);
    }

    void PollClipboard()
    {
        if (!Settings.WatchClipboard || _prompting) return;
        var seq = GetClipboardSequenceNumber();
        if (seq == _clipSeq) return;
        _clipSeq = seq;
        string text;
        try { if (!Clipboard.ContainsText()) return; text = Clipboard.GetText(); }
        catch { return; }   // clipboard locked by another app
        // Only react when the clipboard holds nothing but links to files of a captured type.
        var exts = Settings.CaptureExtensions;
        var urls = UrlOnlyContent(text)?.Where(u => Categories.Matches(u, exts) && !_askedUrls.Contains(u.AbsoluteUri)).ToList();
        if (urls is not { Count: > 0 }) return;
        AskDownload(urls, fromClipboard: true);
    }

    /// Ctrl+V in the main window (explicit action: any http(s) link is accepted).
    public void HandlePaste(string text)
    {
        if (UrlOnlyContent(text) is { Count: > 0 } urls) AskDownload(urls, fromClipboard: false);
    }

    /// The links if every non-empty line is a single http(s) URL; null otherwise.
    static List<Uri>? UrlOnlyContent(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || lines.Length > 50) return null;
        var urls = new List<Uri>();
        foreach (var l in lines)
        {
            if (l.Contains(' ') || !Uri.TryCreate(l, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "") return null;
            if (!urls.Contains(u)) urls.Add(u);
        }
        return urls;
    }

    static bool LooksLikeVideoPage(Uri u)
    {
        if (Categories.Detect(u.AbsolutePath, DownloadKind.File) != FileCategory.Other) return false;
        var host = u.Host.ToLowerInvariant();
        return VideoHosts.Any(h => host == h || host.EndsWith("." + h));
    }

    void AskDownload(List<Uri> urls, bool fromClipboard)
    {
        _askedUrls.AddRange(urls.Select(u => u.AbsoluteUri));
        if (_askedUrls.Count > 50) _askedUrls.RemoveRange(0, _askedUrls.Count - 50);
        _prompting = true;
        bool preferVideo = urls.All(LooksLikeVideoPage);
        string title = urls.Count == 1
            ? (fromClipboard ? S.T("A link was copied. Download it?", "Bir bağlantı kopyalandı. İndirilsin mi?") : S.T("Download the pasted link?", "Yapıştırılan bağlantı indirilsin mi?"))
            : (fromClipboard ? S.T($"{urls.Count} links were copied. Download them?", $"{urls.Count} bağlantı kopyalandı. İndirilsin mi?") : S.T($"Download the {urls.Count} pasted links?", $"Yapıştırılan {urls.Count} bağlantı indirilsin mi?"));
        string body = string.Join("\n", urls.Take(6).Select(u => u.AbsoluteUri)) + (urls.Count > 6 ? "\n…" : "");
        var buttons = preferVideo ? new[] { S.DownloadVideo, S.T("Download as File", "Dosya Olarak İndir") } : new[] { S.Download, S.DownloadVideo };
        var r = Dialogs.Ask(title, body, buttons.Append(S.T("Ignore", "Yoksay")).ToArray(),
            fromClipboard ? S.T("Stop watching the clipboard", "Panoyu izlemeyi bırak") : null);
        _prompting = false;
        _clipSeq = GetClipboardSequenceNumber();
        if (r.Checked) { Settings.WatchClipboard = false; Settings.Save(); }
        if (r.Button is not (0 or 1)) return;
        var kind = (r.Button == 0) == preferVideo ? DownloadKind.Video : DownloadKind.File;
        foreach (var u in urls) Store.Add(new NewDownloadRequest { Url = u, Kind = kind });
    }
}

/// Small modal prompt with custom button labels and an optional checkbox (MessageBox can't relabel buttons).
public static class Dialogs
{
    public static (int Button, bool Checked) Ask(string title, string message, string[] buttons, string? checkbox = null)
    {
        int result = -1;
        var w = new Window
        {
            Title = "MC Download Manager",
            SizeToContent = SizeToContent.WidthAndHeight,
            MaxWidth = 560,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ShowInTaskbar = true,
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        CheckBox? cb = checkbox == null ? null : new CheckBox { Content = checkbox, Margin = new Thickness(0, 0, 0, 14) };
        if (cb != null) panel.Children.Add(cb);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < buttons.Length; i++)
        {
            int idx = i;
            var b = new Button { Content = buttons[i], IsDefault = i == 0, IsCancel = i == buttons.Length - 1, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6) };
            if (i == 0 && Application.Current.TryFindResource("AccentButtonStyle") is Style accent) b.Style = accent;
            b.Click += (_, _) => { result = idx; w.Close(); };
            row.Children.Add(b);
        }
        panel.Children.Add(row);
        w.Content = panel;
        w.ShowDialog();
        return (result, cb?.IsChecked == true);
    }
}
