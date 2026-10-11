using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MCDM;

/// IDM-style per-download window: progress, segment bar, speed, yt-dlp log, quick actions.
public sealed class DetailWindow : Window
{
    static readonly Dictionary<Guid, DetailWindow> Open = new();

    public static void ShowFor(DownloadItem item)
    {
        if (!Open.TryGetValue(item.Id, out var w)) { w = new DetailWindow(item); Open[item.Id] = w; }
        w.Show();
        w.Activate();
    }

    public static void DidComplete(DownloadItem item, bool autoClose)
    {
        if (Open.TryGetValue(item.Id, out var w) && (autoClose || w._autoClose.IsChecked == true)) w.Close();
    }

    readonly DownloadItem _item;
    readonly Border _barFrame = new() { Height = 12, CornerRadius = new CornerRadius(3), ClipToBounds = true };
    readonly Canvas _bar = new() { ClipToBounds = true };
    readonly ProgressBar _progress = new() { Height = 4, Maximum = 1, Margin = new Thickness(0, 14, 0, 10) };
    readonly TextBlock _status = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8) };
    readonly TextBlock _size = new(), _received = new(), _speed = new(), _eta = new(), _connections = new();
    readonly TextBox _log = new() { IsReadOnly = true, Height = 120, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    readonly Button _toggle = new() { MinWidth = 96 };
    readonly Button _open = new() { Content = S.Open, Margin = new Thickness(8, 0, 0, 0) };
    readonly CheckBox _autoClose = new() { Content = S.AutoCloseWhenDone, Margin = new Thickness(0, 14, 0, 14) };
    readonly DispatcherTimer _timer;

    static TextBlock Secondary(string text, double size = 13)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return t;
    }

    DetailWindow(DownloadItem item)
    {
        _item = item;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/app.ico"));
        _autoClose.IsChecked = App.Settings.AutoCloseDetail;

        var p = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };

        // Header: category icon, name, source URL
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var glyph = new TextBlock { Text = item.Glyph, FontSize = 30, Width = 46, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)Application.Current.FindResource("Icons") };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        header.Children.Add(glyph);
        var titles = new StackPanel();
        var name = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetBinding(TextBlock.TextProperty, nameof(DownloadItem.FileName));
        titles.Children.Add(name);
        var url = Secondary(item.Url, 12);
        url.ToolTip = item.Url;
        titles.Children.Add(url);
        header.Children.Add(titles);
        p.Children.Add(header);

        p.Children.Add(_status);

        // Label/value grid, two pairs per row
        var grid = new Grid();
        foreach (var w in new[] { 110.0, 0, 110, 0 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
        var pairs = new (string Label, TextBlock Value)[]
        {
            (S.T("Size", "Boyut"), _size), (S.T("Downloaded", "İndirilen"), _received),
            (S.T("Speed", "Hız"), _speed), (S.T("Time left", "Kalan süre"), _eta),
            (S.T("Connections", "Bağlantılar"), _connections),
        };
        for (int k = 0; k < pairs.Length; k++)
        {
            if (k % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
            var label = Secondary(pairs[k].Label);
            Grid.SetRow(label, k / 2); Grid.SetColumn(label, (k % 2) * 2);
            Grid.SetRow(pairs[k].Value, k / 2); Grid.SetColumn(pairs[k].Value, (k % 2) * 2 + 1);
            grid.Children.Add(label);
            grid.Children.Add(pairs[k].Value);
        }
        p.Children.Add(grid);

        p.Children.Add(_progress);
        _barFrame.SetResourceReference(Border.BackgroundProperty, "ControlStrokeColorDefaultBrush");
        _barFrame.Child = _bar;
        p.Children.Add(_barFrame);
        if (item.Kind == DownloadKind.Video) p.Children.Add(_log);
        p.Children.Add(_autoClose);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (Application.Current.TryFindResource("AccentButtonStyle") is Style accent) _toggle.Style = accent;
        _toggle.Click += (_, _) => { if (_item.IsActive || _item.State == DownloadState.Queued) App.Store.Pause(_item); else App.Store.Resume(_item); };
        row.Children.Add(_toggle);
        AddButton(row, S.Cancel, () => App.Store.Cancel(_item));
        AddButton(row, S.ShowInFolder, () => App.Store.ShowInFolder(_item));
        _open.Click += (_, _) => App.Store.Open(_item);
        row.Children.Add(_open);
        AddButton(row, S.Close, Close);
        p.Children.Add(row);

        DataContext = item;
        Content = p;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(0.5), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _bar.SizeChanged += (_, _) => Refresh();
        Closed += (_, _) => { _timer.Stop(); Open.Remove(item.Id); };
        Refresh();
    }

    static void AddButton(Panel row, string label, Action click)
    {
        var b = new Button { Content = label, Margin = new Thickness(8, 0, 0, 0) };
        b.Click += (_, _) => click();
        row.Children.Add(b);
    }

    void Refresh()
    {
        var i = _item;
        Title = i.State == DownloadState.Downloading && i.TotalBytes > 0 ? $"{(int)(i.Progress * 100)}% · {i.FileName}" : i.FileName;
        _status.Text = i.Stage.Length > 0 && i.State == DownloadState.Downloading ? i.Stage : i.StatusText;
        _status.ToolTip = i.ErrorMessage;
        _size.Text = i.TotalBytes > 0 ? Fmt.Size(i.TotalBytes) : "—";
        _received.Text = Fmt.Size(i.ReceivedBytes) + (i.TotalBytes > 0 ? $" ({i.Progress:P0})" : "");
        _speed.Text = i.Speed > 0 ? Fmt.Speed(i.Speed) : "—";
        _eta.Text = i.Eta is double e ? Fmt.Duration(e) : "—";
        _connections.Text = i.Segments.Length > 0 ? $"{Array.FindAll(i.Segments, s => !s.IsDone).Length} / {i.Segments.Length}" : (i.IsActive ? "1" : "—");
        _progress.Value = i.Progress;
        _toggle.Content = i.IsActive || i.State == DownloadState.Queued ? S.Pause : S.Resume;
        _toggle.IsEnabled = i.State != DownloadState.Completed;
        _open.IsEnabled = i.State == DownloadState.Completed;
        if (i.Kind == DownloadKind.Video && _log.Text.Length != i.Log.Length) { _log.Text = i.Log; _log.ScrollToEnd(); }
        DrawSegments();
    }

    void DrawSegments()
    {
        _bar.Children.Clear();
        double w = _bar.ActualWidth, total = _item.TotalBytes;
        if (w <= 0 || total <= 0) return;
        var fill = (Brush?)TryFindResource("AccentFillColorDefaultBrush") ?? SystemColors.HighlightBrush;
        void Rect(double from, double len)
        {
            var r = new Rectangle { Width = Math.Max(0, len / total * w), Height = _barFrame.Height, Fill = fill };
            Canvas.SetLeft(r, from / total * w);
            _bar.Children.Add(r);
        }
        if (_item.Segments.Length == 0) Rect(0, Math.Min(_item.ReceivedBytes, total));
        foreach (var s in _item.Segments)
        {
            Rect(s.Start, s.Received);
            var tick = new Rectangle { Width = 2, Height = _barFrame.Height, Fill = (Brush?)TryFindResource("SolidBackgroundFillColorBaseBrush") ?? Brushes.White };
            Canvas.SetLeft(tick, s.Start / total * w);
            _bar.Children.Add(tick);
        }
    }
}
