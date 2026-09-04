using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MultiPing.Controls;
using MultiPing.ViewModels;

namespace MultiPing.Views;

public partial class PlotPingView : UserControl
{
    private PlotPingViewModel? _vm;
    private HopLatencyPlotView? _snapshot;

    public PlotPingView()
    {
        InitializeComponent();
        _snapshot = this.FindControl<HopLatencyPlotView>("SnapshotPlot");
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm is not null) _vm.RoundCompleted -= OnRoundCompleted;
        _vm = DataContext as PlotPingViewModel;
        if (_vm is not null) _vm.RoundCompleted += OnRoundCompleted;
    }

    private void OnRoundCompleted()
    {
        if (_vm is not null)
            _snapshot?.Update(_vm.Rows.ToList());
    }

    private void OnTargetKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm is not null)
        {
            _vm.StartRunCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            ShowMruFlyout();
            e.Handled = true;
        }
    }

    private void OnMruButtonClick(object? sender, RoutedEventArgs e) => ShowMruFlyout();

    private void ShowMruFlyout()
    {
        if (this.FindControl<TextBox>("TargetBox") is { } box)
            FlyoutBase.ShowAttachedFlyout(box);
    }

    private void OnMruSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: string host } listBox || _vm is null) return;
        _vm.Target = host;
        listBox.SelectedItem = null;
        if (this.FindControl<TextBox>("TargetBox") is { } box)
        {
            FlyoutBase.GetAttachedFlyout(box)?.Hide();
            box.Focus();
        }
    }
}
