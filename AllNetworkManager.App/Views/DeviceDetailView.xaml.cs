using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace AllNetworkManager.App.Views;

public partial class DeviceDetailView : UserControl
{
    public DeviceDetailView()
    {
        InitializeComponent();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
            e.Handled = true;
        }
        catch { }
    }
}