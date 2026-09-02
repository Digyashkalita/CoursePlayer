using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CoursePlayer.ViewModels;

namespace CoursePlayer.Views;

public partial class HomeView : UserControl
{
    // Fallbacks only; the live brushes are pulled from the themed resource dictionary so the
    // drag highlight follows the selected palette.
    private static readonly Brush IdleBorderBrush =
        new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));

    private static readonly Brush ActiveBorderBrush =
        new SolidColorBrush(Color.FromRgb(0x5E, 0x8B, 0x7E));

    public HomeView()
    {
        InitializeComponent();
    }

    private static Brush ThemedBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var isFileDrop = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = isFileDrop ? DragDropEffects.Copy : DragDropEffects.None;

        if (isFileDrop)
        {
            DropZone.BorderBrush = ThemedBrush("App.Brush.Accent", ActiveBorderBrush);
        }

        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = ThemedBrush("App.Brush.Divider", IdleBorderBrush);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropZone.BorderBrush = ThemedBrush("App.Brush.Divider", IdleBorderBrush);
        e.Handled = true;

        if (DataContext is not HomeViewModel viewModel ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] paths ||
            paths.Length == 0)
        {
            return;
        }

        if (viewModel.DropPathsCommand.CanExecute(paths))
        {
            viewModel.DropPathsCommand.Execute(paths);
        }
    }
}
