using System.Windows.Controls;
using DiskForge.App.ViewModels;

namespace DiskForge.App.Views;

public partial class DashboardPage : UserControl
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            if (viewModel.RefreshCommand.CanExecute(null))
                await viewModel.RefreshCommand.ExecuteAsync(null);
        };
    }
}
