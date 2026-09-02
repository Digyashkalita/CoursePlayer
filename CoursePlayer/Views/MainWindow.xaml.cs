using System.Windows;
using CoursePlayer.Services;
using CoursePlayer.ViewModels;
using MahApps.Metro.Controls;

namespace CoursePlayer.Views;

public partial class MainWindow : MetroWindow
{
    private readonly INavigationService _navigation;

    public MainWindow(INavigationService navigation)
    {
        _navigation = navigation;

        InitializeComponent();

        // The frame only exists after InitializeComponent, so wiring happens here rather
        // than in the service's constructor.
        _navigation.Initialize(ContentFrame);

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (DataContext is not MainWindowViewModel viewModel ||
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
