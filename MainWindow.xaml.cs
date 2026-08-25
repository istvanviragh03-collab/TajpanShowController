using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using TajpanShowController.Core.Models;
using TajpanShowController.ViewModels;

namespace TajpanShowController;

public partial class MainWindow : Window
{
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;
    public MainWindow() => InitializeComponent();
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await ViewModel.InitializeAsync();
    private async void Window_Closing(object? sender, CancelEventArgs e) => await ViewModel.DisposeAsync();
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) ToggleMaximize(); else DragMove(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow(ViewModel) { Owner = this }.ShowDialog();
    private async void AddFiles_Click(object sender, RoutedEventArgs e) => await ChooseFilesAsync();
    private async void DropZone_Click(object sender, MouseButtonEventArgs e) => await ChooseFilesAsync();
    private async Task ChooseFilesAsync() { var d = new OpenFileDialog { Title = "Audiofájlok hozzáadása", Multiselect = true, Filter = "Audiofájlok|*.wav;*.mp3;*.wma;*.aac;*.m4a|Minden fájl|*.*" }; if (d.ShowDialog(this) == true) await ViewModel.AddFilesAsync(d.FileNames); }
    private void Window_DragOver(object sender, DragEventArgs e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
    private async void Window_Drop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) await ViewModel.AddFilesAsync(files); }
    private void PlaylistGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (ViewModel.SelectedTrack is not null) ViewModel.PlayCommand.Execute(null); }
    private async void ClearPlaylist_Click(object sender, RoutedEventArgs e) { if (MessageBox.Show(this, "Biztosan törli a teljes lejátszási listát?", "Lista ürítése", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) await ViewModel.ClearAsync(); }
    private async void SavePlaylist_Click(object sender, RoutedEventArgs e) { var d = new SaveFileDialog { Title = "Lejátszási lista mentése", Filter = "Tajpán playlist (*.json)|*.json", DefaultExt = ".json" }; if (d.ShowDialog(this) == true) await File.WriteAllTextAsync(d.FileName, JsonSerializer.Serialize(ViewModel.Playlist, new JsonSerializerOptions { WriteIndented = true })); }
    private async void OpenPlaylist_Click(object sender, RoutedEventArgs e) { var d = new OpenFileDialog { Title = "Lejátszási lista megnyitása", Filter = "Tajpán playlist (*.json)|*.json" }; if (d.ShowDialog(this) != true) return; try { var tracks = JsonSerializer.Deserialize<List<PlaylistTrack>>(await File.ReadAllTextAsync(d.FileName)) ?? []; await ViewModel.ClearAsync(); await ViewModel.AddFilesAsync(tracks.Select(t => t.FilePath)); } catch (Exception ex) { MessageBox.Show(this, "A playlist nem olvasható: " + ex.Message, "Hiba", MessageBoxButton.OK, MessageBoxImage.Error); } }
}
