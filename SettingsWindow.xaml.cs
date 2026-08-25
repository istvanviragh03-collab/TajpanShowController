using System.Windows;
using TajpanShowController.ViewModels;
namespace TajpanShowController;
public partial class SettingsWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    public SettingsWindow(MainWindowViewModel viewModel) { InitializeComponent(); _viewModel = viewModel; DataContext = viewModel; }
    private void Refresh_Click(object sender, RoutedEventArgs e) => _viewModel.RefreshPorts();
    private async void SimButton_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: string bits }) await _viewModel.SimulateButtonAsync(bits); }
    private void Malformed_Click(object sender, RoutedEventArgs e) => _viewModel.SimulateMalformed();
    private void Nack_Click(object sender, RoutedEventArgs e) => _viewModel.SimulateNack();
    private void Loss_Click(object sender, RoutedEventArgs e) => _viewModel.SimulateConnectionLoss();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
