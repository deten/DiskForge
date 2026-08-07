using DiskForge.App.ViewModels;
using Wpf.Ui.Controls;

namespace DiskForge.App.Views;

public partial class CloneWindow : FluentWindow
{
    private readonly CloneDialogViewModel _viewModel;

    public CloneWindow(CloneDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Add_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _viewModel.Confirm();
        DialogResult = true;
        Close();
    }
}
