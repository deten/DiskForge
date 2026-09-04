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

    /// <summary>
    /// Pushes the typed size into the view model. WPF-UI 3.0.5's NumberBox parses typed text into
    /// Value only on commit and its TwoWay binding does not carry that to the source, so without this
    /// a size typed into the box is discarded in favour of whatever the slider last showed.
    /// </summary>
    private void ResizeBox_ValueChanged(object sender, System.Windows.RoutedEventArgs e)
        => ResizeBox.GetBindingExpression(NumberBox.ValueProperty)?.UpdateSource();

    private void Resize_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        // Commit the box first, in case it still holds keyboard focus.
        ResizeBox.GetBindingExpression(NumberBox.ValueProperty)?.UpdateSource();

        // Only close on a valid op; otherwise StageError explains the block and the dialog stays open.
        if (_viewModel.BuildResizeOperation() is not { } op) return;
        _viewModel.StagedOps.Add(op);
        DialogResult = true;
        Close();
    }

    private void Check_Click(object sender, System.Windows.RoutedEventArgs e) => StageCheck(repair: false);
    private void Repair_Click(object sender, System.Windows.RoutedEventArgs e) => StageCheck(repair: true);

    private void StageCheck(bool repair)
    {
        // Only close on a valid op; otherwise StageError explains the block and the dialog stays open.
        if (_viewModel.BuildCheckOperation(repair) is not { } op) return;
        _viewModel.StagedOps.Add(op);
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
