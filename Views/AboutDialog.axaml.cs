using System.Reflection;
using Avalonia.Controls;

namespace MultiPing.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();

        var assembly = typeof(App).Assembly;
        var version =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion?.Split('+')[0]
            ?? assembly.GetName().Version?.ToString()
            ?? "Unknown";

        VersionText.Text = $"MultiPing {version}";
    }
}
