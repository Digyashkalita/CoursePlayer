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
    private readonly IThumbnailService _thumbnails;
    private readonly ILogger<HomeViewModel> _logger;

    /// <summary>Cancels in-flight cover generation when the library reloads.</summary>
    private CancellationTokenSource? _coverCts;

    public HomeViewModel(
        IDatabaseWriter database,
        IImportCoordinator import,
        INavigationService navigation,
        IThumbnailService thumbnails,
        ILogger<HomeViewModel> logger)
    {
        _database = database;
        _import = import;
        _navigation = navigation;
        _thumbnails = thumbnails;
        _logger = logger;
        Title = "Home";

        // A course imported from anywhere (sidebar or drop zone) should appear here without
        // the user having to leave and re-enter Home.
        _import.ImportCompleted += OnImportCompleted;
    }

    public ObservableCollection<CourseCardViewModel> Courses { get; } = [];

    public bool HasCourses => Courses.Count > 0;

    [RelayCommand]
    private Task ImportFolderAsync() => _import.StartFromFolderPickerAsync();

    /// <summary>Opens a course's detail view when its card is clicked.</summary>
    [RelayCommand]
    private async Task OpenCourseAsync(CourseCardViewModel? course)
    {
        if (course is null)
        {
            return;
        }

        try
        {
            await _navigation.NavigateToAsync<CourseDetailViewModel>(course.CourseId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open course {CourseId}.", course.CourseId);
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
                    .Select(c => new CourseCardData(
                        c.Id,
                        c.Title,
                        c.FolderPath,
                        c.ThumbnailPath,
                        c.Assets.Count(a => a.Type == AssetType.Video),
                        c.Assets.Count))
                    .ToListAsync(token),
                cancellationToken);

            Courses.Clear();
            foreach (var course in courses)
            {
                Courses.Add(new CourseCardViewModel(
                    course,
                    // A cover generated on an earlier visit is shown right away; the rest
                    // are filled in by the background pass below.
                    _thumbnails.GetCourseThumbnailPath(course.Id, course.ThumbnailPath)));
            }

            OnPropertyChanged(nameof(HasCourses));
            _logger.LogDebug("Home loaded {Count} course(s).", Courses.Count);

            StartCoverGeneration();
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

    /// <summary>
    /// Fills in any card still missing a cover, one course at a time, after the grid is
    /// already on screen. Courses whose cover exists are skipped without touching ffmpeg.
    /// </summary>
    private void StartCoverGeneration()
    {
        var pending = Courses.Where(c => !c.HasThumbnail).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        _coverCts?.Cancel();
        _coverCts?.Dispose();
        _coverCts = new CancellationTokenSource();

        var token = _coverCts.Token;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    foreach (var card in pending)
                    {
                        token.ThrowIfCancellationRequested();

                        // Only the course cover is needed here: the per-lesson covers are
                        // generated when the course itself is opened.
                        var cover = await _thumbnails
                            .EnsureCourseThumbnailAsync(card.CourseId, token)
                            .ConfigureAwait(false);

                        if (cover is null)
                        {
                            continue;
                        }

                        var dispatcher = Application.Current?.Dispatcher;
                        if (dispatcher is null || dispatcher.CheckAccess())
                        {
                            card.ThumbnailPath = cover;
                        }
                        else
                        {
                            // Fire-and-forget on purpose: the generator must not wait on the
                            // UI thread to finish painting before starting the next course.
                            _ = dispatcher.InvokeAsync(() => card.ThumbnailPath = cover);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Navigated away or shutting down.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Course cover generation failed.");
                }
            },
            token);
    }
}

/// <summary>The projection Home needs for one card — no entity graph, no lazy loads.</summary>
public sealed record CourseCardData(
    int Id,
    string Title,
    string FolderPath,
    string? ThumbnailPath,
    int VideoCount,
    int AssetCount);

/// <summary>One course card in the Home grid.</summary>
public partial class CourseCardViewModel : ObservableObject
{
    public CourseCardViewModel(CourseCardData data, string? thumbnailPath)
    {
        CourseId = data.Id;
        Title = data.Title;
        FolderPath = data.FolderPath;
        Summary = BuildSummary(data.VideoCount, data.AssetCount);
        _thumbnailPath = thumbnailPath;
    }

    public int CourseId { get; }

    public string Title { get; }

    public string FolderPath { get; }

    /// <summary>Caption under the title, e.g. "8 videos · 17 documents".</summary>
    public string Summary { get; }

    /// <summary>
    /// Absolute path to this course's cover, or null while none exists. Assigned from the
    /// background generator so a card gains its artwork without the page reloading.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private string? _thumbnailPath;

    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailPath);

    private static string BuildSummary(int videoCount, int assetCount)
    {
        var others = assetCount - videoCount;

        var parts = new List<string>(2);
        if (videoCount > 0) parts.Add($"{videoCount} {(videoCount == 1 ? "video" : "videos")}");
        if (others > 0) parts.Add($"{others} {(others == 1 ? "document" : "documents")}");

        return parts.Count == 0 ? "Empty course" : string.Join(" · ", parts);
    }
}
