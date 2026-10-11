using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MCDM;

public sealed class SettingsWindow : Window
{
    static SettingsWindow? _open;

    public static void ShowSingle()
    {
        _open ??= new SettingsWindow();
        _open.Show();
        _open.Activate();
    }

    readonly AppSettings _s = App.Settings;

    SettingsWindow()
    {
        Title = S.Settings + " — MC Download Manager";
        Width = 600;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/app.ico"));
        int oldPort = _s.ServerPort;
        Closed += (_, _) =>
        {
            _open = null;
            _s.Save();
            if (_s.ServerPort != oldPort) App.Bridge.Start(_s.ServerPort);
            App.Store.PumpQueue();
        };
        Content = new TabControl
        {
            Margin = new Thickness(8),
            Items =
            {
                Tab(S.T("General", "Genel"), General()),
                Tab(S.T("File Types", "Dosya Türleri"), FileTypes()),
                Tab(S.T("Video", "Video"), Video()),
                Tab(S.T("Browser", "Tarayıcı"), Browser()),
            },
        };
    }

    static TabItem Tab(string header, UIElement content) =>
        new() { Header = header, Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };

    static StackPanel Page() => new() { Margin = new Thickness(14) };
    static TextBlock Label(string t, bool bold = false) =>
        new() { Text = t, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };

    static CheckBox Check(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
        c.Click += (_, _) => set(c.IsChecked == true);
        return c;
    }

    static ComboBox Combo<T>((string Label, T Value)[] options, T current, Action<T> set)
    {
        var c = new ComboBox { ItemsSource = options.Select(o => o.Label).ToList(), Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        c.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => Equals(o.Value, current)));
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) set(options[c.SelectedIndex].Value); };
        return c;
    }

    static (string, int)[] Range(int from, int to) => Enumerable.Range(from, to - from + 1).Select(i => (i.ToString(), i)).ToArray();

    UIElement General()
    {
        var p = Page();
        p.Children.Add(Label(S.T("Download folder", "İndirme klasörü"), true));
        var row = new DockPanel();
        var browse = new Button { Content = S.T("Browse…", "Gözat…"), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        var dir = new TextBox { Text = _s.DownloadDirectory, VerticalContentAlignment = VerticalAlignment.Center };
        dir.LostFocus += (_, _) => { if (dir.Text.Trim().Length > 0) _s.DownloadDirectory = dir.Text.Trim(); };
        browse.Click += (_, _) =>
        {
            var d = new OpenFolderDialog { InitialDirectory = Directory.Exists(_s.DownloadDirectory) ? _s.DownloadDirectory : null };
            if (d.ShowDialog(this) == true) dir.Text = _s.DownloadDirectory = d.FolderName;
        };
        row.Children.Add(browse);
        row.Children.Add(dir);
        p.Children.Add(row);

        p.Children.Add(Label(S.T("Connections per download", "İndirme başına bağlantı"), true));
        p.Children.Add(Combo(Range(1, 16), _s.SegmentCount, v => _s.SegmentCount = v));
        p.Children.Add(Label(S.T("Simultaneous downloads", "Aynı anda indirme"), true));
        p.Children.Add(Combo(Range(1, 10), _s.MaxConcurrent, v => _s.MaxConcurrent = v));

        p.Children.Add(Label(""));
        p.Children.Add(Check(S.T("Sort into category subfolders (Video, Music, …)", "Kategori alt klasörlerine ayır (Video, Müzik, …)"), _s.OrganizeByCategory, v => _s.OrganizeByCategory = v));
        p.Children.Add(Check(S.T("Start downloads immediately", "İndirmeleri hemen başlat"), _s.AutoStart, v => _s.AutoStart = v));
        p.Children.Add(Check(S.T("Notify when a download completes", "İndirme bitince bildir"), _s.NotifyOnComplete, v => _s.NotifyOnComplete = v));
        p.Children.Add(Check(S.T("Show the download window when a download starts", "İndirme başlayınca indirme penceresini göster"), _s.ShowDetailOnStart, v => _s.ShowDetailOnStart = v));
        p.Children.Add(Check(S.AutoCloseWhenDone, _s.AutoCloseDetail, v => _s.AutoCloseDetail = v));
        p.Children.Add(Check(S.T("Watch the clipboard for download links", "Panodaki indirme bağlantılarını izle"), _s.WatchClipboard, v => _s.WatchClipboard = v));
        p.Children.Add(Check(S.T("Start with Windows (in the system tray)", "Windows ile başlat (sistem tepsisinde)"), App.StartWithWindows, v => App.StartWithWindows = v));
        return p;
    }

    UIElement FileTypes()
    {
        var p = Page();
        p.Children.Add(Label(S.T("Browser downloads and copied links of these types are handed to MC Download Manager.",
            "Bu türlerdeki tarayıcı indirmeleri ve kopyalanan bağlantılar MC Download Manager'a aktarılır.")));
        foreach (var g in Categories.CaptureGroups)
        {
            p.Children.Add(Check(S.CategoryLabel(g.Category), _s.CaptureGroupIds.Contains(g.Id), v =>
            {
                _s.CaptureGroupIds.Remove(g.Id);
                if (v) _s.CaptureGroupIds.Add(g.Id);
            }));
            p.Children.Add(new TextBlock { Text = string.Join(", ", g.Extensions), Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(22, 0, 0, 4), TextWrapping = TextWrapping.Wrap });
        }
        p.Children.Add(Label(S.T("Additional extensions (comma separated)", "Ek uzantılar (virgülle ayırın)"), true));
        var custom = new TextBox { Text = _s.CustomExtensions };
        custom.TextChanged += (_, _) => _s.CustomExtensions = custom.Text;
        p.Children.Add(custom);
        return p;
    }

    UIElement Video()
    {
        var p = Page();
        p.Children.Add(Label(S.T("Quality", "Kalite"), true));
        p.Children.Add(Combo(new[]
        {
            (S.T("Best available", "En iyi"), 0), ("2160p (4K)", 2160), ("1440p", 1440), ("1080p", 1080), ("720p", 720), ("480p", 480), ("360p", 360),
            (S.T("Audio only (M4A)", "Yalnızca ses (M4A)"), -1), (S.T("Audio only (MP3)", "Yalnızca ses (MP3)"), -2),
        }, _s.VideoMaxHeight, v => _s.VideoMaxHeight = v));
        p.Children.Add(Label(S.T("Container", "Kapsayıcı"), true));
        p.Children.Add(Combo(new[] { ("MP4", "mp4"), ("MKV", "mkv"), ("WebM", "webm"), (S.T("Original", "Orijinal"), "original") }, _s.VideoContainer, v => _s.VideoContainer = v));
        p.Children.Add(Label(S.T("Custom yt-dlp format (-f), overrides the options above", "Özel yt-dlp formatı (-f), yukarıdaki seçenekleri geçersiz kılar"), true));
        var fmt = new TextBox { Text = _s.VideoFormat };
        fmt.TextChanged += (_, _) => _s.VideoFormat = fmt.Text;
        p.Children.Add(fmt);

        p.Children.Add(Label(S.T("Tools", "Araçlar"), true));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Gray };
        void RefreshStatus() => Task.Run(() =>
        {
            string V(string? path) => path == null ? S.T("not installed", "kurulu değil") : (ToolInstaller.Version(path) ?? "?");
            var text = $"yt-dlp: {V(VideoDownloader.LocateYtDlp(_s.YtdlpPath))}\nffmpeg: {V(VideoDownloader.LocateFfmpeg())}\ndeno: {V(VideoDownloader.LocateDeno())}";
            Dispatcher.InvokeAsync(() => status.Text = text);
        });
        RefreshStatus();
        p.Children.Add(status);
        var install = new Button { Content = S.T("Install / update tools", "Araçları kur / güncelle"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 4) };
        var msg = new TextBlock { TextWrapping = TextWrapping.Wrap };
        install.Click += async (_, _) =>
        {
            install.IsEnabled = false;
            try { await ToolInstaller.InstallAsync(true, new Progress<string>(m => msg.Text = m)); }
            catch (Exception e) { msg.Text = S.T("Install failed: ", "Kurulum başarısız: ") + e.Message; }
            install.IsEnabled = true;
            RefreshStatus();
        };
        p.Children.Add(install);
        p.Children.Add(msg);
        p.Children.Add(Label(S.T("Downloaded from the official GitHub releases of yt-dlp, yt-dlp/FFmpeg-Builds and deno; SHA-256 checksums are verified.",
            "yt-dlp, yt-dlp/FFmpeg-Builds ve deno'nun resmi GitHub sürümlerinden indirilir; SHA-256 sağlama toplamları doğrulanır.")));
        p.Children.Add(Label(S.T("Custom yt-dlp.exe path (optional)", "Özel yt-dlp.exe yolu (isteğe bağlı)"), true));
        var path = new TextBox { Text = _s.YtdlpPath };
        path.TextChanged += (_, _) => _s.YtdlpPath = path.Text.Trim();
        p.Children.Add(path);
        return p;
    }

    UIElement Browser()
    {
        var p = Page();
        p.Children.Add(Label(S.T("Bridge", "Köprü"), true));
        p.Children.Add(new TextBlock { Text = App.Bridge.StatusText, Foreground = System.Windows.Media.Brushes.Gray });
        var seen = App.Bridge.Connections.Where(c => (DateTime.Now - c.Value).TotalMinutes < 2).Select(c => c.Key).ToList();
        p.Children.Add(new TextBlock { Text = S.T("Connected extensions: ", "Bağlı uzantılar: ") + (seen.Count > 0 ? string.Join(", ", seen) : "—"), Foreground = System.Windows.Media.Brushes.Gray });
        p.Children.Add(Label(S.T("Port (the extension popup must use the same port)", "Port (uzantı açılır penceresinde de aynı port olmalı)")));
        p.Children.Add(Combo(new[] { ("48213", 48213), ("48214", 48214), ("48215", 48215), ("48216", 48216) }, _s.ServerPort, v => _s.ServerPort = v));

        p.Children.Add(Label(S.T("Install the extension", "Uzantıyı kur"), true));
        p.Children.Add(Label(S.T(
            "Chrome / Edge / Brave:\n1. Open chrome://extensions (edge://extensions in Edge).\n2. Turn on Developer mode.\n3. Click “Load unpacked” and select the folder below.\n\nFirefox: about:debugging ▸ This Firefox ▸ Load Temporary Add-on ▸ pick manifest.json in the folder (temporary until a signed build is available).",
            "Chrome / Edge / Brave:\n1. chrome://extensions adresini açın (Edge'de edge://extensions).\n2. Geliştirici modunu açın.\n3. “Paketlenmemiş öğe yükle”ye tıklayıp aşağıdaki klasörü seçin.\n\nFirefox: about:debugging ▸ Bu Firefox ▸ Geçici Eklenti Yükle ▸ klasördeki manifest.json (imzalı sürüm gelene kadar geçici).")));
        p.Children.Add(new TextBox { Text = App.ExtensionDir, IsReadOnly = true, Margin = new Thickness(0, 6, 0, 6) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var open = new Button { Content = S.T("Open Folder", "Klasörü Aç") };
        open.Click += (_, _) => { if (Directory.Exists(App.ExtensionDir)) System.Diagnostics.Process.Start("explorer.exe", $"\"{App.ExtensionDir}\""); };
        var copy = new Button { Content = S.T("Copy Path", "Yolu Kopyala") };
        copy.Click += (_, _) => { try { Clipboard.SetText(App.ExtensionDir); } catch { } };
        row.Children.Add(open);
        row.Children.Add(copy);
        p.Children.Add(row);
        return p;
    }
}
