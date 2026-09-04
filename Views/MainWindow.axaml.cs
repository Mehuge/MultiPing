using Avalonia.Controls;
using Avalonia.Interactivity;
using MultiPing.ViewModels;

namespace MultiPing.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnOptionsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MonitorViewModelBase vm)
        {
            var optionsVm = new OptionsViewModel(vm.Settings, vm.ConfigSvc, vm);
            var dialog = new OptionsDialog { DataContext = optionsVm };
            await dialog.ShowDialog(this);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MonitorViewModelBase vm)
        {
            vm.Stop();
            vm.SaveSettings();
        }
        base.OnClosing(e);
    }
}
