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
