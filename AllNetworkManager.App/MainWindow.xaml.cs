using System.Windows;
using System.Windows.Input;

namespace AllNetworkManager.App;

public partial class MainWindow : Window
{
    public MainWindow(ViewModels.MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += (_, _) => viewModel.FlushNotes();
    }

    /// <summary>Keeps the device column as wide as the list minus the status column, so "Status" never clips.</summary>
    private void DeviceList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListView list) return;
        if (list.View is not System.Windows.Controls.GridView grid || grid.Columns.Count < 2) return;

        var statusWidth = grid.Columns[^1].Width;
        var padding = 28; // item padding + grid lines
        grid.Columns[0].Width = Math.Max(180, list.ActualWidth - statusWidth - padding);
    }

    private void DeviceItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm && vm.SelectedDevice != null)
        {
            vm.OpenWebInterfaceCommand.Execute(null);
        }
    }
}