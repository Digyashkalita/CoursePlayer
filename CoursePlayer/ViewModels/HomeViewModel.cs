using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CoursePlayer.Data;
using CoursePlayer.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoursePlayer.ViewModels;

/// <summary>
/// The course library. Phase 1 shows the empty state and the drop zone; Phase 3 fills in
/// the card grid.
/// </summary>
public partial class HomeViewModel : PageViewModelBase, INavigationAware
{
    private readonly IDatabaseWriter _database;
    private readonly IImportCoordinator _import;
    private readonly INavigationService _navigation;
    private readonly ILogger<HomeViewModel> _logger;

    public HomeViewModel(
        IDatabaseWriter database,
        IImportCoordinator import,
        INavigationService navigation,
        ILogger<HomeViewModel> logger)
    {
        _database = database;
        _import = import;
        _navigation = navigation;
        _logger = logger;
        Title = "Home";

        // A course imported from anywhere (sidebar or drop zone) should appear here without
        // the user having to leave and re-enter Home.
        _import.ImportCompleted += OnImportCompleted;
    }

    public ObservableCollection<Course> Courses { get; } = [];

    public bool HasCourses => Courses.Count > 0;

    [RelayCommand]
    private Task ImportFolderAsync() => _import.StartFromFolderPickerAsync();

    /// <summary>Opens a course's detail view when its card is clicked.</summary>
    [RelayCommand]
    private async Task OpenCourseAsync(Course? course)
    {
        if (course is null)
        {
            return;
        }

        try
        {
            await _navigation.NavigateToAsync<CourseDetailViewModel>(course.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open course {CourseId}.", course.Id);
            ErrorMessage = "Could not open that course. See the log for details.";
        }
    }

    [RelayCommand]
    private Task DropPathsAsync(IReadOnlyList<string>? paths) =>
        paths is null ? Task.CompletedTask : _import.StartFromPathsAsync(paths);

    public Task OnNavigatedToAsync(object? parameter, CancellationToken cancellationToken = default) =>
        LoadAsync(cancellationToken);

    // ImportCompleted fires on a background thread; hop to the UI thread before touching the
    // observable collection, mirroring how NotificationService marshals.
    private void OnImportCompleted(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            _ = LoadAsync();
        }
        else
        {
            dispatcher.InvokeAsync(() => _ = LoadAsync());
        }
    }

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var courses = await _database.QueryAsync(
                (context, token) => context.Courses
                    .AsNoTracking()
                    .OrderByDescending(c => c.LastOpenedAt ?? c.ImportedAt)
                    .ToListAsync(token),
                cancellationToken);

            Courses.Clear();
            foreach (var course in courses)
            {
                Courses.Add(course);
            }

            OnPropertyChanged(nameof(HasCourses));
            _logger.LogDebug("Home loaded {Count} course(s).", Courses.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load the course library.");
            ErrorMessage = "Could not read the course library. See the log for details.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
