using System.Linq;
using Avalonia.Controls;
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
}
