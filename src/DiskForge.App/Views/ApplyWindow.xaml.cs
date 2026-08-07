using DiskForge.App.ViewModels;
using Wpf.Ui.Controls;

namespace DiskForge.App.Views;

public partial class ApplyWindow : FluentWindow
{
    public ApplyWindow(ApplyViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
