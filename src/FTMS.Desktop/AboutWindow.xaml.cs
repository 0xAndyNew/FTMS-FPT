using System.Reflection;
using System.Windows;

namespace FTMS.Desktop;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is not null)
        {
            VersionText.Text = $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
