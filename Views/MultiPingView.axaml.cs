using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MultiPing.Controls;
using MultiPing.ViewModels;

namespace MultiPing.Views;

public partial class MultiPingView : UserControl
{
    private MultiPingViewModel? _vm;
    private HopLatencyPlotView? _snapshot;

    public MultiPingView()
    {
        InitializeComponent();
        _snapshot = this.FindControl<HopLatencyPlotView>("SnapshotPlot");
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm is not null) _vm.RoundCompleted -= OnRoundCompleted;
        _vm = DataContext as MultiPingViewModel;
        if (_vm is not null) _vm.RoundCompleted += OnRoundCompleted;
    }

    private void OnRoundCompleted()
    {
        if (_vm is not null)
            _snapshot?.Update(_vm.SelectedTraceHops.ToList());
    }
}
