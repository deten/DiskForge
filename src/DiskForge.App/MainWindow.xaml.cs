using System.Windows;
using DiskForge.App.ViewModels;
using DiskForge.App.Views;
using Wpf.Ui.Controls;

namespace DiskForge.App;

public partial class MainWindow : FluentWindow
{
    private readonly DashboardViewModel _dashboardViewModel;

    public MainWindow(DashboardViewModel dashboardViewModel)
    {
        InitializeComponent();
        _dashboardViewModel = dashboardViewModel;
        ContentHost.Content = new DashboardPage(dashboardViewModel);
    }

    // Clone is a modal flow rather than a page, so open it and return the nav selection to Dashboard.
    private void CloneNav_Click(object sender, RoutedEventArgs e)
    {
        if (_dashboardViewModel.StartCloneCommand.CanExecute(null))
            _dashboardViewModel.StartCloneCommand.Execute(null);
        DashboardNav.IsChecked = true;
    }
}
