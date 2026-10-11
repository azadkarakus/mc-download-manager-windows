using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace MCDM;

public sealed class SidebarEntry : INotifyPropertyChanged
{
    public required string Label { get; init; }
    public required string Glyph { get; init; }
    public required Func<DownloadItem, bool> Match { get; init; }
    public Thickness Indent { get; init; }
    public string CountText { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void SetCount(int n)
    {
        var t = n > 0 ? n.ToString() : "";
        if (t == CountText) return;
        CountText = t;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
    }
}

public partial class MainWindow : Window
{
    readonly ListCollectionView _view;
    readonly List<SidebarEntry> _entries;
    Func<DownloadItem, bool> _sidebarFilter = _ => true;

    public MainWindow()
    {
        InitializeComponent();
        if (TryFindResource("AccentButtonStyle") is Style accent) AddButton.Style = accent;

        // Same order as the macOS sidebar: All, categories, Finished, Unfinished.
        _entries = new List<SidebarEntry> { new() { Label = S.T("All", "Tümü"), Glyph = "", Match = _ => true } };
        _entries.AddRange(Enum.GetValues<FileCategory>().Select(c => new SidebarEntry
            { Label = S.CategoryLabel(c), Glyph = Categories.Glyph(c), Match = i => i.Category == c, Indent = new Thickness(14, 0, 0, 0) }));
        _entries.Add(new() { Label = S.T("Finished", "Bitenler"), Glyph = "", Match = i => i.State == DownloadState.Completed });
        _entries.Add(new() { Label = S.T("Unfinished", "Bitmeyenler"), Glyph = "", Match = i => i.State != DownloadState.Completed });
        Sidebar.ItemsSource = _entries;
        Sidebar.SelectedIndex = 0;

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(App.Store.Items);
        _view.Filter = o => o is DownloadItem i && _sidebarFilter(i) && MatchesSearch(i);
        _view.IsLiveFiltering = true;
        _view.LiveFilteringProperties.Add(nameof(DownloadItem.State));
        _view.LiveFilteringProperties.Add(nameof(DownloadItem.FileName));
        Grid.ItemsSource = _view;
        App.Store.Items.CollectionChanged += (_, _) => UpdateStatus();

        InputBindings.Add(new KeyBinding(new RelayCommand(Paste), Key.V, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(ShowAddDialog), Key.N, ModifierKeys.Control));
        new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus(), Dispatcher);
        IsVisibleChanged += (_, _) => UpdateStatus();
    }

    /// Closing the window keeps the app running in the tray.
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    bool MatchesSearch(DownloadItem i)
    {
        var q = SearchBox.Text.Trim();
        return q.Length == 0 || i.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Url.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    void UpdateStatus()
    {
        if (!IsVisible) return;
        var s = App.Store;
        foreach (var e in _entries) e.SetCount(e == _entries[0] ? 0 : s.Items.Count(e.Match));
        EmptyState.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActiveText.Text = s.ActiveCount.ToString();
        SpeedText.Text = Fmt.Speed(s.TotalSpeed);
        BridgeText.Text = App.Bridge.Running ? S.T("Browser bridge is running", "Tarayıcı köprüsü çalışıyor") : App.Bridge.StatusText;
        BridgeDot.Fill = (System.Windows.Media.Brush)(TryFindResource(App.Bridge.Running ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush")
            ?? (App.Bridge.Running ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Red));
    }

    IEnumerable<DownloadItem> Selected => Grid.SelectedItems.Cast<DownloadItem>().ToList();

    void Sidebar_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Sidebar.SelectedItem is SidebarEntry entry) { _sidebarFilter = entry.Match; _view?.Refresh(); if (_view != null) UpdateStatus(); }
    }

    void Search_Changed(object sender, TextChangedEventArgs e) { _view?.Refresh(); if (_entries != null) UpdateStatus(); }

    void Paste()
    {
        if (Keyboard.FocusedElement is TextBox) { ApplicationCommands.Paste.Execute(null, (IInputElement)Keyboard.FocusedElement); return; }
        try { if (Clipboard.ContainsText()) ((App)Application.Current).HandlePaste(Clipboard.GetText()); } catch { }
    }

    public void ShowAddDialog()
    {
        var w = new Window
        {
            Title = S.AddDownload, Owner = IsVisible ? this : null, Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = S.PasteLinks, Margin = new Thickness(0, 0, 0, 6) });
        var box = new TextBox { AcceptsReturn = true, Height = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.NoWrap };
        try { if (Clipboard.ContainsText() && Clipboard.GetText().TrimStart().StartsWith("http")) box.Text = Clipboard.GetText().Trim(); } catch { }
        panel.Children.Add(box);
        var opts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 14) };
        opts.Children.Add(new TextBlock { Text = S.Connections, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var seg = new ComboBox { ItemsSource = Enumerable.Range(1, 16).ToList(), SelectedItem = App.Settings.SegmentCount, Width = 60 };
        opts.Children.Add(seg);
        panel.Children.Add(opts);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DownloadKind? kind = null;
        foreach (var (label, k) in new[] { (S.Download, DownloadKind.File), (S.DownloadVideo, DownloadKind.Video) })
        {
            var b = new Button { Content = label, IsDefault = k == DownloadKind.File, Margin = new Thickness(0, 0, 8, 0) };
            if (k == DownloadKind.File && TryFindResource("AccentButtonStyle") is Style accent) b.Style = accent;
            b.Click += (_, _) => { kind = k; w.Close(); };
            row.Children.Add(b);
        }
        var cancel = new Button { Content = S.Cancel, IsCancel = true, Margin = new Thickness(0) };
        row.Children.Add(cancel);
        panel.Children.Add(row);
        w.Content = panel;
        box.Focus();
        box.SelectAll();
        w.ShowDialog();
        if (kind is DownloadKind dk) App.Store.AddUrls(box.Text, dk, (int)seg.SelectedItem);
    }

    void Add_Click(object s, RoutedEventArgs e) => ShowAddDialog();
    void Resume_Click(object s, RoutedEventArgs e) { foreach (var i in Selected) App.Store.Resume(i); }
    void Pause_Click(object s, RoutedEventArgs e) { foreach (var i in Selected) App.Store.Pause(i); }
    void Cancel_Click(object s, RoutedEventArgs e) { foreach (var i in Selected) App.Store.Cancel(i); }
    void Remove_Click(object s, RoutedEventArgs e) { foreach (var i in Selected) App.Store.Remove(i); }
    void RemoveDelete_Click(object s, RoutedEventArgs e)
    {
        var items = Selected.ToList();
        if (items.Count == 0) return;
        var r = MessageBox.Show(this, S.T($"Move {items.Count} file(s) to the Recycle Bin and remove them from the list?", $"{items.Count} dosya Geri Dönüşüm Kutusu'na taşınıp listeden kaldırılsın mı?"),
            "MC Download Manager", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.OK) foreach (var i in items) App.Store.Remove(i, deleteFile: true);
    }
    void Open_Click(object s, RoutedEventArgs e) { foreach (var i in Selected) App.Store.Open(i); }
    void Folder_Click(object s, RoutedEventArgs e) { if (Selected.FirstOrDefault() is { } i) App.Store.ShowInFolder(i); }
    void Details_Click(object s, RoutedEventArgs e) { foreach (var i in Selected) DetailWindow.ShowFor(i); }
    void CopyLink_Click(object s, RoutedEventArgs e) { try { Clipboard.SetText(string.Join("\n", Selected.Select(i => i.Url))); } catch { } }
    void PauseAll_Click(object s, RoutedEventArgs e) => App.Store.PauseAll();
    void ResumeAll_Click(object s, RoutedEventArgs e) => App.Store.ResumeAll();
    void ClearFinished_Click(object s, RoutedEventArgs e) => App.Store.ClearFinished();
    void Settings_Click(object s, RoutedEventArgs e) => SettingsWindow.ShowSingle();

    void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is not DownloadItem i) return;
        if (i.State == DownloadState.Completed) App.Store.Open(i); else DetailWindow.ShowFor(i);
    }

    void Grid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { Remove_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Enter) { Grid_DoubleClick(sender, null!); e.Handled = true; }
    }
}

sealed class RelayCommand : ICommand
{
    readonly Action _run;
    public RelayCommand(Action run) => _run = run;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => _run();
}
