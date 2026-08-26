using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TajpanShowController.Core.Models;
using TajpanShowController.Infrastructure.Persistence;
using TajpanShowController.ViewModels;

namespace TajpanShowController;

public partial class MainWindow : Window
{
    private Point _dragStart;
    private PlaylistTrack? _draggedTrack;
    private string? _playlistFilePath;
    private bool _debugAutoScroll = true;
    private readonly PlaylistFileStore _playlistStore = new();
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(SeekSlider_DragStarted));
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(SeekSlider_DragCompleted));
        ViewModel.DiagnosticsFlushed += ViewModel_DiagnosticsFlushed;
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ViewModel.InitializeAsync();
    private async void Window_Closing(object? sender, CancelEventArgs e) => await ViewModel.DisposeAsync();
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) ToggleMaximize(); else if (e.OriginalSource is not Button) DragMove(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void PlaybackTab_Click(object sender, RoutedEventArgs e) => ShowPage(PlaybackPage, PlaybackTab);
    private void SettingsTab_Click(object sender, RoutedEventArgs e) => ShowPage(SettingsPage, SettingsTab);
    private void DebugTab_Click(object sender, RoutedEventArgs e) => ShowPage(DebugPage, DebugTab);
    private void ShowPage(UIElement page, Button activeTab)
    {
        PlaybackPage.Visibility = SettingsPage.Visibility = DebugPage.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
        foreach (var button in new[] { PlaybackTab, SettingsTab, DebugTab }) { button.Background = Brushes.Transparent; button.Foreground = (Brush)FindResource("MutedBrush"); }
        activeTab.Background = new SolidColorBrush(Color.FromRgb(0x12, 0x17, 0x1C)); activeTab.Foreground = Brushes.White;
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e) => await ChooseFilesAsync();
    private async Task ChooseFilesAsync()
    {
        var dialog = new OpenFileDialog { Title = "Audiofájlok hozzáadása", Multiselect = true, Filter = "Audiofájlok|*.wav;*.mp3;*.wma;*.aac;*.m4a|Minden fájl|*.*" };
        if (dialog.ShowDialog(this) == true) await ViewModel.AddFilesAsync(dialog.FileNames);
    }
    private void Window_DragOver(object sender, DragEventArgs e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    private async void Window_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await ViewModel.AddFilesAsync(files); }
    private void PlaylistGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (ViewModel.SelectedTrack is not null) ViewModel.PlayCommand.Execute(null); }

    private async void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!SeekSlider.IsEnabled || SeekSlider.ActualWidth <= 0 || FindAncestor<Thumb>(e.OriginalSource as DependencyObject) is not null) return;
        e.Handled = true;
        var fraction = Math.Clamp(e.GetPosition(SeekSlider).X / SeekSlider.ActualWidth, 0, 1);
        await ViewModel.SeekToFractionAsync(fraction);
    }

    private void SeekSlider_DragStarted(object sender, DragStartedEventArgs e) => ViewModel.BeginSeek();
    private async void SeekSlider_DragCompleted(object sender, DragCompletedEventArgs e) => await ViewModel.CompleteSeekAsync();

    private async void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Playlist.Count > 0 && MessageBox.Show(this, "Új playlist létrehozásakor a jelenlegi lista törlődik. Folytatja?", "New playlist", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await ViewModel.ClearAsync(); _playlistFilePath = null; ViewModel.PlaylistName = "Untitled Show"; ViewModel.PlaylistPath = "Nincs megnyitott playlist fájl";
    }
    private async void OpenPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Playlist megnyitása", Filter = "Tajpán playlist (*.json)|*.json|Minden fájl|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var folder = Path.GetDirectoryName(dialog.FileName)!;
            var document = await _playlistStore.LoadAsync(dialog.FileName);
            var files = document.Tracks.Select(x => Path.GetFullPath(Path.Combine(folder, x)));
            await ViewModel.ClearAsync(); await ViewModel.AddFilesAsync(files); SetPlaylistFile(dialog.FileName);
            ViewModel.SetPlaylistNameFromStorage(document.PlaylistName!);
        }
        catch (Exception ex) { MessageBox.Show(this, "A playlist nem olvasható: " + ex.Message, "Hiba", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async void SavePlaylist_Click(object sender, RoutedEventArgs e) { if (_playlistFilePath is null) await SavePlaylistAsAsync(); else await WritePlaylistAsync(_playlistFilePath); }
    private async void SaveAsPlaylist_Click(object sender, RoutedEventArgs e) => await SavePlaylistAsAsync();
    private async Task SavePlaylistAsAsync()
    {
        var dialog = new SaveFileDialog { Title = "Playlist mentése", Filter = "Tajpán playlist (*.json)|*.json", DefaultExt = ".json", FileName = ViewModel.PlaylistName };
        if (dialog.ShowDialog(this) != true) return; await WritePlaylistAsync(dialog.FileName); SetPlaylistFile(dialog.FileName);
    }
    private async Task WritePlaylistAsync(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var document = new PlaylistDocument { PlaylistName = ViewModel.PlaylistName, Tracks = ViewModel.Playlist.Select(t => Path.GetRelativePath(folder, t.FilePath)).ToList() };
        await _playlistStore.SaveAsync(path, document);
        ViewModel.MarkPlaylistSaved();
    }
    private void SetPlaylistFile(string path) { _playlistFilePath = path; ViewModel.PlaylistPath = path; }

    private void Playlist_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { _dragStart = e.GetPosition(PlaylistList); _draggedTrack = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource)?.DataContext as PlaylistTrack; }
    private void Playlist_MouseMove(object sender, MouseEventArgs e) { if (e.LeftButton != MouseButtonState.Pressed || _draggedTrack is null || (e.GetPosition(PlaylistList) - _dragStart).Length < SystemParameters.MinimumHorizontalDragDistance) return; DragDrop.DoDragDrop(PlaylistList, _draggedTrack, DragDropEffects.Move); }
    private void Playlist_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(typeof(PlaylistTrack)) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; }
    private void Playlist_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PlaylistTrack)) is not PlaylistTrack source) return;
        var target = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource)?.DataContext as PlaylistTrack;
        if (target is null || ReferenceEquals(source, target)) return;
        var oldIndex = ViewModel.Playlist.IndexOf(source); var newIndex = ViewModel.Playlist.IndexOf(target);
        if (oldIndex >= 0 && newIndex >= 0) { ViewModel.Playlist.Move(oldIndex, newIndex); ViewModel.MarkPlaylistModified(); }
    }
    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject { while (current is not null && current is not T) current = VisualTreeHelper.GetParent(current); return current as T; }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => ViewModel.RefreshPorts();
    private async void SimButton_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string bits }) await ViewModel.SimulateButtonAsync(bits); }
    private void Malformed_Click(object sender, RoutedEventArgs e) => ViewModel.SimulateMalformed();
    private void Nack_Click(object sender, RoutedEventArgs e) => ViewModel.SimulateNack();
    private void Loss_Click(object sender, RoutedEventArgs e) => ViewModel.SimulateConnectionLoss();

    private void DebugLog_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0) return;
        _debugAutoScroll = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 1;
    }

    private void ViewModel_DiagnosticsFlushed(object? sender, EventArgs e)
    {
        if (!_debugAutoScroll || DebugPage.Visibility != Visibility.Visible || ViewModel.Diagnostics.Count == 0) return;
        DebugLogList.ScrollIntoView(ViewModel.Diagnostics[^1]);
    }

}
