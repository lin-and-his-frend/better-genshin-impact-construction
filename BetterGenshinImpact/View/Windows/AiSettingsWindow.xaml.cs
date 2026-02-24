using System.Windows;

namespace BetterGenshinImpact.View.Windows;

public partial class AiSettingsWindow
{
    public AiSettingsWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
