using DiskForge.App.ViewModels;
using Wpf.Ui.Controls;

namespace DiskForge.App.Views;

public partial class CreatePartitionWindow : FluentWindow
{
    private readonly CreatePartitionDialogViewModel _viewModel;

    public CreatePartitionWindow(CreatePartitionDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// Pushes the size box's committed value into the view model.
    ///
    /// WPF-UI 3.0.5's <c>NumberBox</c> parses typed text into <c>Value</c> only when the box commits
    /// (focus loss / Enter), and its TwoWay binding does not carry that to the source — the source's
    /// old value wins instead. The visible symptom was that a size typed into the box was ignored and
    /// the partition was created at whatever the slider last showed. Forcing the binding to update
    /// here is what makes the typed value authoritative; the slider still drives the box because that
    /// is the source→target direction, which was never broken.
    /// </summary>
    private void SizeBox_ValueChanged(object sender, System.Windows.RoutedEventArgs e)
        => SizeBox.GetBindingExpression(NumberBox.ValueProperty)?.UpdateSource();

    private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Add_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        // Belt and braces: if the box still has keyboard focus (Enter on the default button), it has
        // not committed yet, so commit it before reading the plan.
        SizeBox.GetBindingExpression(NumberBox.ValueProperty)?.UpdateSource();
        _viewModel.Confirm();
        DialogResult = true;
        Close();
    }
}
