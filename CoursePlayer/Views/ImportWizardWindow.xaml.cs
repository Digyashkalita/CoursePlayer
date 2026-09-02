using CoursePlayer.ViewModels;
using MahApps.Metro.Controls;

namespace CoursePlayer.Views;

public partial class ImportWizardWindow : MetroWindow
{
    public ImportWizardWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ImportWizardViewModel oldViewModel)
        {
            oldViewModel.RequestClose -= OnRequestClose;
        }

        if (e.NewValue is ImportWizardViewModel newViewModel)
        {
            newViewModel.RequestClose += OnRequestClose;
        }
    }

    private void OnRequestClose(object? sender, System.EventArgs e)
    {
        // Mirror the VM's decision onto the modal's DialogResult so ShowDialog() returns it.
        if (DataContext is ImportWizardViewModel viewModel)
        {
            DialogResult = viewModel.DialogResult;
        }

        Close();
    }
}
