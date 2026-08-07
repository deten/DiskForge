using DiskForge.App.ViewModels;
using Wpf.Ui.Controls;

namespace DiskForge.App.Views;

public partial class PartitionDetailsWindow : FluentWindow
{
    private readonly PartitionDetailsViewModel _viewModel;

    public PartitionDetailsWindow(PartitionDetailsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Stage_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_viewModel.TryStageChanges())
        {
            DialogResult = true;
            Close();
        }
        // else: StageError is shown, keep the dialog open
    }

    private void Format_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _viewModel.RequestFormatNow();
        DialogResult = true;
        Close();
    }

    private void Delete_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        // Only close on a valid op; otherwise StageError explains the block and the dialog stays open.
        if (_viewModel.BuildDeleteOperation() is null) return;
        _viewModel.RequestDeleteNow();
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
