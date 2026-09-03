using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CoursePlayer.Data;
using CoursePlayer.Services;
using MaterialDesignThemes.Wpf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoursePlayer.ViewModels;

/// <summary>
/// The course-detail screen. Opened from a Home card with the course id; loads the course
/// and its assets and groups them by their imported folder hierarchy (sections). This is
/// where the Phase 2 probe metadata (duration, resolution) first becomes visible.
/// </summary>
public partial class CourseDetailViewModel : PageViewModelBase, INavigationAware, INavigatedFromAware, IDisposable
{
    private readonly IDatabaseWriter _database;
    private readonly INavigationService _navigation;
    private readonly IThumbnailService _thumbnails;
    private readonly ILogger<CourseDetailViewModel> _logger;

    /// <summary>Cancels in-flight cover generation when the user leaves the page.</summary>
    private CancellationTokenSource? _thumbnailCts;

    private bool _isDisposed;

    public CourseDetailViewModel(
        IDatabaseWriter database,
        INavigationService navigation,
        IThumbnailService thumbnails,
        ILogger<CourseDetailViewModel> logger)
    {
        _database = database;
        _navigation = navigation;
        _thumbnails = thumbnails;
        _logger = logger;
        Title = "Course";

        // Initialize the PlayVideoCommand
        PlayVideoCommand = new AsyncRelayCommand<object>(PlayVideoAsync);
    }

    public ObservableCollection<CourseSectionViewModel> Sections { get; } = [];

    [ObservableProperty]
    private string _courseTitle = string.Empty;

    [ObservableProperty]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    private string _summary = string.Empty;

    public bool CanGoBack => _navigation.CanGoBack;

    [RelayCommand]
    private Task BackAsync() => _navigation.GoBackAsync();

    /// <summary>
    /// Command to play a video asset.
    /// </summary>
    public IAsyncRelayCommand<object> PlayVideoCommand { get; }

    private async Task PlayVideoAsync(object? parameter)
    {
        if (parameter is CourseAssetViewModel assetVm)
        {
            await _navigation.NavigateToAsync<VideoPlayerViewModel>(assetVm.AssetId);
        }
    }

    public Task OnNavigatedToAsync(object? parameter, CancellationToken cancellationToken = default)
    {
        if (parameter is int courseId)
        {
            return LoadAsync(courseId, cancellationToken);
        }

        ErrorMessage = "No course was specified.";
        return Task.CompletedTask;
    }

    private async Task LoadAsync(int courseId, CancellationToken cancellationToken)
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var course = await _database.QueryAsync(
                (context, token) => context.Courses
                    .AsNoTracking()
                    .Include(c => c.Assets)
                    .FirstOrDefaultAsync(c => c.Id == courseId, token),
                cancellationToken);

            if (course is null)
            {
                ErrorMessage = "This course could not be found. It may have been removed.";
                return;
            }

            CourseTitle = course.Title;
            Title = course.Title;
            FolderPath = course.FolderPath;

            var assets = course.Assets.OrderBy(a => a.OrderIndex).ToList();
            Summary = BuildSummary(assets);

            Sections.Clear();
            foreach (var group in GroupIntoSections(assets, _thumbnails))
            {
                Sections.Add(group);
            }

            _logger.LogDebug(
                "Course {CourseId} loaded with {AssetCount} asset(s) in {SectionCount} section(s).",
                courseId,
                assets.Count,
                Sections.Count);

            // Covers are generated after the rows are on screen: the page must never wait on
            // ffmpeg. Each finished cover is pushed straight into its row.
            StartThumbnailGeneration(courseId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load course {CourseId}.", courseId);
            ErrorMessage = "Could not open this course. See the log for details.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Kicks off background cover generation for the course and feeds each result back into
    /// the matching row. Cancels any run still in flight from a previous course.
    /// </summary>
    private void StartThumbnailGeneration(int courseId)
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = new CancellationTokenSource();

        var token = _thumbnailCts.Token;

        // Index the rows once so each callback is a dictionary hit rather than a scan.
        var rows = Sections
            .SelectMany(s => s.Assets)
            .ToDictionary(a => a.AssetId);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await _thumbnails.GenerateForCourseAsync(
                        courseId,
                        onAssetThumbnail: (assetId, path) =>
                        {
                            if (!rows.TryGetValue(assetId, out var row))
                            {
                                return;
                            }

                            // Generation runs on a worker thread; the binding target does not.
                            var dispatcher = Application.Current?.Dispatcher;
                            if (dispatcher is null || dispatcher.CheckAccess())
                            {
                                row.ThumbnailPath = path;
                            }
                            else
                            {
                                dispatcher.InvokeAsync(() => row.ThumbnailPath = path);
                            }
                        },
                        cancellationToken: token);
                }
                catch (OperationCanceledException)
                {
                    // Left the page; nothing to report.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Cover generation failed for course {CourseId}.", courseId);
                }
            },
            token);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
    }

    /// <summary>
    /// Stops cover generation when the shell navigates away, so opening a lesson does not
    /// leave ffmpeg grinding through the rest of the course.
    /// </summary>
    public Task OnNavigatedFromAsync()
    {
        _thumbnailCts?.Cancel();
        return Task.CompletedTask;
    }

    // Assets arrive pre-ordered by (section, filename), so grouping by section in
    // first-appearance order keeps sections in tree order with root-level files first.
    private static IEnumerable<CourseSectionViewModel> GroupIntoSections(
        IReadOnlyList<Asset> assets,
        IThumbnailService thumbnails)
    {
        var groups = new List<CourseSectionViewModel>();
        var byName = new Dictionary<string, CourseSectionViewModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets)
        {
            var key = string.IsNullOrEmpty(asset.Section) ? string.Empty : asset.Section!;
            if (!byName.TryGetValue(key, out var group))
            {
                // A root-level file has no folder; label its bucket so the header still reads.
                var name = key.Length == 0 ? "Course root" : key.Replace("/", " › ");
                group = new CourseSectionViewModel(name);
                byName[key] = group;
                groups.Add(group);
            }

            // A cover generated on an earlier visit shows immediately; the rest stream in.
            group.Assets.Add(new CourseAssetViewModel(asset, thumbnails.GetAssetThumbnailPath(asset.Id)));
        }

        // If everything sits in the root there is only one, unlabelled-feeling group; still
        // fine to show. Refresh each group's caption now that its assets are in.
        foreach (var group in groups)
        {
            group.RefreshSummary();
        }

        return groups;
    }

    private static string BuildSummary(IReadOnlyList<Asset> assets)
    {
        var videos = assets.Count(a => a.Type == AssetType.Video);
        var others = assets.Count - videos;
        var sections = assets
            .Select(a => a.Section)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var parts = new List<string>(3);
        if (videos > 0) parts.Add($"{videos} {(videos == 1 ? "video" : "videos")}");
        if (others > 0) parts.Add($"{others} {(others == 1 ? "document" : "documents")}");
        if (sections > 1) parts.Add($"{sections} sections");

        return parts.Count == 0 ? "Empty course" : string.Join(" · ", parts);
    }
}

/// <summary>One section (an imported subfolder) and the assets inside it.</summary>
public partial class CourseSectionViewModel : ObservableObject
{
    public CourseSectionViewModel(string name) => Name = name;

    public string Name { get; }

    public ObservableCollection<CourseAssetViewModel> Assets { get; } = [];

    [ObservableProperty]
    private string _summary = string.Empty;

    public void RefreshSummary()
    {
        var count = Assets.Count;
        Summary = $"{count} {(count == 1 ? "item" : "items")}";
    }
}

/// <summary>A single asset row in the detail view.</summary>
public partial class CourseAssetViewModel : ObservableObject
{
    public CourseAssetViewModel(Asset asset, string? thumbnailPath = null)
    {
        Title = asset.Title;
        IsOnline = asset.IsOnline;
        Icon = IconFor(asset.Type);
        Duration = asset.Duration is { } d ? FormatDuration(d) : null;
        Resolution = asset.Resolution;
        AssetId = asset.Id;
        _thumbnailPath = thumbnailPath;
    }

    public string Title { get; }

    public bool IsOnline { get; }

    public PackIconKind Icon { get; }

    /// <summary>Formatted length, or null for documents / unprobed videos.</summary>
    public string? Duration { get; }

    public string? Resolution { get; }

    public int AssetId { get; }

    /// <summary>
    /// Absolute path to this asset's cover, or null while none exists. Set from the
    /// background generator as covers finish, so the row swaps its icon for artwork
    /// without the page reloading.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private string? _thumbnailPath;

    /// <summary>Drives which of the two visuals the row shows: artwork or a type icon.</summary>
    public bool HasThumbnail => !string.IsNullOrEmpty(ThumbnailPath);

    private static PackIconKind IconFor(AssetType type) => type switch
    {
        AssetType.Video => PackIconKind.PlayCircleOutline,
        AssetType.Pdf => PackIconKind.FilePdfBox,
        AssetType.Docx => PackIconKind.FileWordBox,
        AssetType.Text => PackIconKind.FileDocumentOutline,
        _ => PackIconKind.FileOutline,
    };

    private static string FormatDuration(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}"
            : $"{value.Minutes}:{value.Seconds:D2}";
}