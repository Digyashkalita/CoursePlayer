using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CoursePlayer.Services;
using MaterialDesignThemes.Wpf;
using Microsoft.Extensions.Logging;

namespace CoursePlayer.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly INotificationService _notifications;
    private readonly IImportCoordinator _import;
    private readonly IFFmpegBootstrapper _ffmpeg;
    private readonly ILogger<MainWindowViewModel> _logger;

    public MainWindowViewModel(
        INavigationService navigation,
        INotificationService notifications,
        IImportCoordinator import,
        IFFmpegBootstrapper ffmpeg,
        ILogger<MainWindowViewModel> logger)
    {
        _navigation = navigation;
        _notifications = notifications;
        _import = import;
        _ffmpeg = ffmpeg;
        _logger = logger;

        NavigationItems =
        [
            new NavigationItem("Home", PackIconKind.Home, typeof(HomeViewModel)),
            new NavigationItem("Favorites", PackIconKind.Heart, typeof(FavoritesViewModel)),
            new NavigationItem("Recent", PackIconKind.History, typeof(RecentViewModel)),
            new NavigationItem("Search", PackIconKind.Magnify, typeof(SearchViewModel)),
            new NavigationItem("Settings", PackIconKind.Cog, typeof(SettingsViewModel)),
        ];

        _navigation.Navigated += OnNavigated;
    }

    public ObservableCollection<NavigationItem> NavigationItems { get; }

    public SnackbarMessageQueue MessageQueue => _notifications.MessageQueue;

    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    [ObservableProperty]
    private NavigationItem? _selectedNavigationItem;

    /// <summary>Non-empty when FFmpeg is unusable; shown as a dismissible banner.</summary>
    [ObservableProperty]
    private string? _mediaEngineWarning;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarExpanded = !IsSidebarExpanded;

    [RelayCommand]
    private void DismissMediaEngineWarning() => MediaEngineWarning = null;

    /// <summary>Navigates to the section the user clicked in the sidebar.</summary>
    [RelayCommand]
    private async Task NavigateAsync(NavigationItem? item)
    {
        if (item is null || item.ViewModelType == _navigation.CurrentViewModelType)
        {
            return;
        }

        try
        {
            await _navigation.NavigateToAsync(item.ViewModelType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Navigation to {Section} failed.", item.Label);
            _notifications.Show($"Could not open {item.Label}.");
        }
    }

    /// <summary>Sidebar "+ Import Folder" button.</summary>
    [RelayCommand]
    private Task ImportFolderAsync() => _import.StartFromFolderPickerAsync();

    /// <summary>Handles files or folders dropped anywhere on the shell.</summary>
    [RelayCommand]
    private Task DropPathsAsync(IReadOnlyList<string>? paths) =>
        paths is null ? Task.CompletedTask : _import.StartFromPathsAsync(paths);

    /// <summary>Called by the shell once the frame is wired up.</summary>
    public async Task InitializeAsync()
    {
        if (!_ffmpeg.IsAvailable)
        {
            MediaEngineWarning = _ffmpeg.StatusMessage;
        }

        // Navigating first means OnNavigated sets the sidebar selection, so we don't
        // navigate twice (once from the selection change, once from here).
        await _navigation.NavigateToAsync<HomeViewModel>();
    }

    /// <summary>Selecting a sidebar row navigates. Set by the ListBox two-way binding.</summary>
    partial void OnSelectedNavigationItemChanged(NavigationItem? value)
    {
        if (value is not null)
        {
            NavigateCommand.Execute(value);
        }
    }

    private void OnNavigated(object? sender, Type viewModelType)
    {
        // Keeps the sidebar highlight in step with navigation that did not start there
        // (opening a course, going back out of the player).
        var match = NavigationItems.FirstOrDefault(i => i.ViewModelType == viewModelType);
        if (match is not null)
        {
            SelectedNavigationItem = match;
        }
    }
}
