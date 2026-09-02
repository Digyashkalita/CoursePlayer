using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CoursePlayer.Data;
using CoursePlayer.Services;
using MaterialDesignThemes.Wpf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoursePlayer.ViewModels;

/// <summary>
/// The video player page. Owns playback state for one asset, the course playlist beside it,
/// and the resume/complete bookkeeping written back to SQLite.
/// </summary>
/// <remarks>
/// Switching lessons happens <em>in place</em> rather than by navigating to a fresh instance of
/// this page: the FFME element is a single shared visual, and re-parenting it on every lesson
/// change tears playback down and back up for no benefit.
/// </remarks>
public partial class VideoPlayerViewModel : PageViewModelBase, INavigationAware, INavigatedFromAware, IDisposable
{
    /// <summary>Watched fraction at which an asset counts as finished.</summary>
    private const double CompletionThreshold = 0.95d;

    /// <summary>Resume points below this are treated as "start from the beginning".</summary>
    private static readonly TimeSpan MinimumResumePosition = TimeSpan.FromSeconds(3);

    /// <summary>Never resume into the last few seconds; that would instantly re-complete.</summary>
    private static readonly TimeSpan ResumeEndGuard = TimeSpan.FromSeconds(5);

    /// <summary>How close to the end the clock must be for an end-of-stream event to count.</summary>
    private static readonly TimeSpan EndOfStreamTolerance = TimeSpan.FromSeconds(3);

    /// <summary>A lesson must have been playing this long before it may auto-advance.</summary>
    private static readonly TimeSpan MinimumPlayTimeBeforeAutoAdvance = TimeSpan.FromSeconds(5);

    /// <summary>Scrubber moves smaller than this are the clock catching up, not a seek.</summary>
    private static readonly TimeSpan ScrubberSeekEpsilon = TimeSpan.FromSeconds(1.5);

    private static readonly string[] SubtitleExtensions = [".srt", ".vtt", ".ass", ".ssa"];

    private readonly IAssetPlaybackService _playback;
    private readonly IDatabaseWriter _database;
    private readonly INavigationService _navigation;
    private readonly INotificationService _notifications;
    private readonly ILogger<VideoPlayerViewModel> _logger;

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _controlsTimer;

    private Asset? _asset;
    private int _courseId;
    private int _currentIndex = -1;
    private TimeSpan _resumePosition;
    private DateTime _playbackStartedAtUtc = DateTime.MaxValue;
    private bool _resumeApplied;
    private bool _completionRecorded;
    private bool _isSwitchingAsset;
    private bool _isClockWritingPosition;
    private bool _isLeaving;
    private bool _playbackEventsDetached;
    private bool _isDisposed;

    public VideoPlayerViewModel(
        IAssetPlaybackService playback,
        IDatabaseWriter database,
        INavigationService navigation,
        INotificationService notifications,
        ILogger<VideoPlayerViewModel> logger)
    {
        _playback = playback;
        _database = database;
        _navigation = navigation;
        _notifications = notifications;
        _logger = logger;

        Title = "Player";

        SpeedOptions =
        [
            new PlaybackSpeedOption(0.5d),
            new PlaybackSpeedOption(0.75d),
            new PlaybackSpeedOption(1d),
            new PlaybackSpeedOption(1.25d),
            new PlaybackSpeedOption(1.5d),
            new PlaybackSpeedOption(2d),
        ];

        _selectedSpeed = SpeedOptions.First(o => o.Value == 1d);
        _volumePercent = Math.Round(_playback.Volume * 100d);
        _isMuted = _playback.IsMuted;

        // 4 Hz is smooth enough for a scrubber without burning the UI thread.
        _clockTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
        _clockTimer.Tick += OnClockTick;

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoSaveTimer.Tick += OnAutoSaveTick;

        _controlsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _controlsTimer.Tick += OnControlsTimerTick;

        _playback.MediaOpened += OnMediaOpened;
        _playback.MediaEnded += OnMediaEnded;
        _playback.MediaFailed += OnMediaFailed;
    }

    /// <summary>The shared renderer; the view parents this into its own tree.</summary>
    public Unosquare.FFME.MediaElement? MediaElement => _playback.MediaElement;

    public ObservableCollection<PlaylistItemViewModel> Playlist { get; } = [];

    public IReadOnlyList<PlaybackSpeedOption> SpeedOptions { get; }

    [ObservableProperty]
    private string _courseTitle = string.Empty;

    [ObservableProperty]
    private string _assetTitle = string.Empty;

    /// <summary>"4K", "1080p", ... or empty when the frame size is unknown.</summary>
    [ObservableProperty]
    private string _resolutionBadge = string.Empty;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private PackIconKind _playPauseIcon = PackIconKind.Play;

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    private string _positionText = "0:00";

    [ObservableProperty]
    private string _durationText = "0:00";

    /// <summary>0..1 read-ahead fill, painted behind the scrubber.</summary>
    [ObservableProperty]
    private double _bufferingProgress;

    [ObservableProperty]
    private double _volumePercent;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private PackIconKind _volumeIcon = PackIconKind.VolumeHigh;

    [ObservableProperty]
    private PlaybackSpeedOption _selectedSpeed;

    [ObservableProperty]
    private bool _hasSubtitles;

    [ObservableProperty]
    private bool _areSubtitlesEnabled = true;

    [ObservableProperty]
    private bool _isPlaylistVisible = true;

    [ObservableProperty]
    private bool _areControlsVisible = true;

    [ObservableProperty]
    private bool _isFullscreen;

    [ObservableProperty]
    private bool _isSeekable = true;

    /// <summary>True while the user is dragging the scrubber; the clock stops writing to it.</summary>
    public bool IsScrubbing { get; private set; }

    public bool CanGoToPrevious => FindNeighbour(-1) >= 0;

    public bool CanGoToNext => FindNeighbour(1) >= 0;
    // ------------------------------- navigation -------------------------------

    public async Task OnNavigatedToAsync(object? parameter, CancellationToken cancellationToken = default)
    {
        if (parameter is not int assetId)
        {
            ErrorMessage = "No lesson was specified.";
            return;
        }

        await LoadAssetAsync(assetId, isFirstLoad: true, "navigation", cancellationToken);
    }

    public async Task OnNavigatedFromAsync()
    {
        _isLeaving = true;

        _clockTimer.Stop();
        _autoSaveTimer.Stop();
        _controlsTimer.Stop();

        await SaveProgressAsync(CancellationToken.None).ConfigureAwait(true);

        // Detach before closing: FFME raises end-of-stream while a container is torn down,
        // and this page is finished, so it must not react to that.
        DetachPlaybackEvents();

        await _playback.CloseAsync().ConfigureAwait(true);

        SetPlayingState(false);
    }

    /// <summary>
    /// Loads and starts one asset. Used both for the initial navigation and for every later
    /// lesson change, which is why it flushes the outgoing asset's progress first.
    /// </summary>
    private async Task LoadAssetAsync(int assetId, bool isFirstLoad, string reason, CancellationToken cancellationToken)
    {
        if (_isLeaving || _isSwitchingAsset)
        {
            _logger.LogDebug(
                "Ignoring load of asset {AssetId} ({Reason}): leaving={Leaving}, switching={Switching}.",
                assetId,
                reason,
                _isLeaving,
                _isSwitchingAsset);
            return;
        }

        _logger.LogDebug("Loading asset {AssetId} ({Reason}).", assetId, reason);

        _isSwitchingAsset = true;
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            if (!isFirstLoad)
            {
                // The clock is about to belong to a different file.
                _clockTimer.Stop();
                _autoSaveTimer.Stop();
                await SaveProgressAsync(cancellationToken);
            }

            var asset = await _database.QueryAsync(
                (context, token) => context.Assets
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == assetId, token),
                cancellationToken);

            if (asset is null)
            {
                ErrorMessage = "This lesson could not be found. It may have been removed.";
                return;
            }

            var courseChanged = _courseId != asset.CourseId;

            _asset = asset;
            _courseId = asset.CourseId;
            AssetTitle = asset.Title;
            Title = asset.Title;
            ResolutionBadge = BadgeForResolution(asset.Resolution);

            if (isFirstLoad || courseChanged)
            {
                await LoadCourseContextAsync(cancellationToken);
            }
            else
            {
                MarkCurrentPlaylistItem();
            }

            if (!File.Exists(asset.FilePath))
            {
                ErrorMessage = "The file for this lesson is missing from disk.";
                return;
            }

            _resumePosition = await ReadResumePositionAsync(asset.Id, cancellationToken);
            await StartPlaybackAsync(asset);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open asset {AssetId} in the player.", assetId);
            ErrorMessage = "Could not open this lesson. See the log for details.";
        }
        finally
        {
            IsBusy = false;
            _isSwitchingAsset = false;
        }
    }

    private async Task LoadCourseContextAsync(CancellationToken cancellationToken)
    {
        var course = await _database.QueryAsync(
            (context, token) => context.Courses
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == _courseId, token),
            cancellationToken);

        CourseTitle = course?.Title ?? string.Empty;

        var rows = await _database.QueryAsync(
            (context, token) => context.Assets
                .AsNoTracking()
                .Where(a => a.CourseId == _courseId)
                .OrderBy(a => a.OrderIndex)
                .Select(a => new PlaylistRow
                {
                    Id = a.Id,
                    Title = a.Title,
                    Type = a.Type,
                    Duration = a.Duration,
                    IsOnline = a.IsOnline,
                    Completed = a.Progress != null && a.Progress.Completed,
                })
                .ToListAsync(token),
            cancellationToken);

        Playlist.Clear();
        foreach (var row in rows)
        {
            Playlist.Add(new PlaylistItemViewModel(row));
        }

        MarkCurrentPlaylistItem();
    }

    private void MarkCurrentPlaylistItem()
    {
        var assetId = _asset?.Id;
        _currentIndex = -1;

        for (var i = 0; i < Playlist.Count; i++)
        {
            var isCurrent = Playlist[i].AssetId == assetId;
            Playlist[i].IsCurrent = isCurrent;

            if (isCurrent)
            {
                _currentIndex = i;
            }
        }

        NotifyPlaylistBounds();
    }

    private async Task<TimeSpan> ReadResumePositionAsync(int assetId, CancellationToken cancellationToken)
    {
        var seconds = await _database.QueryAsync(
            (context, token) => context.Progresses
                .AsNoTracking()
                .Where(p => p.AssetId == assetId && !p.Completed)
                .Select(p => (double?)p.WatchedSeconds)
                .FirstOrDefaultAsync(token),
            cancellationToken);

        return seconds is { } value && value > MinimumResumePosition.TotalSeconds
            ? TimeSpan.FromSeconds(value)
            : TimeSpan.Zero;
    }

    private async Task StartPlaybackAsync(Asset asset)
    {
        _resumeApplied = false;
        _completionRecorded = false;
        _playbackStartedAtUtc = DateTime.MaxValue;

        // The previous container may still be open and sitting at its own end position, so
        // these resets must not be read as user input or as progress on the new lesson.
        _isClockWritingPosition = true;
        try
        {
            PositionSeconds = 0d;
            DurationSeconds = asset.Duration is { } known ? known.TotalSeconds : 0d;
        }
        finally
        {
            _isClockWritingPosition = false;
        }

        PositionText = FormatTime(TimeSpan.Zero);
        DurationText = FormatTime(asset.Duration ?? TimeSpan.Zero);
        BufferingProgress = 0d;
        HasSubtitles = false;

        // An external subtitle track can only be attached while the container opens.
        _playback.SubtitlesSource = FindSidecarSubtitle(asset.FilePath);
        _playback.AreSubtitlesEnabled = AreSubtitlesEnabled;

        var opened = await _playback.OpenAsync(asset.FilePath);
        if (!opened)
        {
            ErrorMessage = "This video could not be decoded. Try another lesson or re-import the course.";
            return;
        }

        await _playback.PlayAsync();

        _playbackStartedAtUtc = DateTime.UtcNow;
        SetPlayingState(true);

        _clockTimer.Start();
        _autoSaveTimer.Start();
        RestartControlsTimer();

        _logger.LogDebug("Playing asset {AssetId} ({Title}).", asset.Id, asset.Title);
    }

    private static string? FindSidecarSubtitle(string mediaPath)
    {
        try
        {
            foreach (var extension in SubtitleExtensions)
            {
                var candidate = Path.ChangeExtension(mediaPath, extension);
                if (candidate is not null && File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (ArgumentException)
        {
            // Unusable path characters: just play without subtitles.
        }

        return null;
    }

    // --------------------------------- commands --------------------------------

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        if (!_playback.IsOpen)
        {
            return;
        }

        if (_playback.IsPlaying)
        {
            await _playback.PauseAsync();
            SetPlayingState(false);
            _autoSaveTimer.Stop();
            await SaveProgressAsync(CancellationToken.None);
        }
        else
        {
            await _playback.PlayAsync();
            SetPlayingState(true);
            _autoSaveTimer.Start();
        }
    }

    [RelayCommand]
    private Task RewindAsync() => SeekRelativeAsync(TimeSpan.FromSeconds(-10));

    [RelayCommand]
    private Task ForwardAsync() => SeekRelativeAsync(TimeSpan.FromSeconds(10));

    [RelayCommand(CanExecute = nameof(CanGoToPrevious))]
    private Task PreviousAsync() => GoToNeighbourAsync(-1, announceEdge: true, "previous button");

    [RelayCommand(CanExecute = nameof(CanGoToNext))]
    private Task NextAsync() => GoToNeighbourAsync(1, announceEdge: true, "next button");

    [RelayCommand]
    private void ToggleMute()
    {
        IsMuted = !IsMuted;
        NotifyActivity();
    }

    [RelayCommand]
    private void ToggleSubtitles()
    {
        AreSubtitlesEnabled = !AreSubtitlesEnabled;
        NotifyActivity();
    }

    [RelayCommand]
    private void TogglePlaylist()
    {
        IsPlaylistVisible = !IsPlaylistVisible;
        NotifyActivity();
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        IsFullscreen = !IsFullscreen;
        NotifyActivity();
    }

    [RelayCommand]
    private async Task BackAsync()
    {
        if (_courseId > 0)
        {
            await _navigation.NavigateToAsync<CourseDetailViewModel>(_courseId);
        }
        else if (_navigation.CanGoBack)
        {
            await _navigation.GoBackAsync();
        }
    }

    [RelayCommand]
    private async Task OpenPlaylistItemAsync(PlaylistItemViewModel? item)
    {
        if (item is null || item.AssetId == _asset?.Id)
        {
            return;
        }

        if (!item.IsOnline || !item.IsPlayable)
        {
            _notifications.Show(item.IsOnline
                ? "Only video lessons open in the player for now."
                : $"\"{item.Title}\" is missing from disk.");
            return;
        }

        NotifyActivity();
        await LoadAssetAsync(item.AssetId, isFirstLoad: false, "playlist click", CancellationToken.None);
    }

    [RelayCommand]
    private void StepSpeed(string? direction)
    {
        var index = IndexOfSpeed(SelectedSpeed.Value);
        var next = string.Equals(direction, "down", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(0, index - 1)
            : Math.Min(SpeedOptions.Count - 1, index + 1);

        if (next == index)
        {
            return;
        }

        SelectedSpeed = SpeedOptions[next];
        _notifications.Show($"Speed {SelectedSpeed.Label}");
        NotifyActivity();
    }

    private int IndexOfSpeed(double value)
    {
        for (var i = 0; i < SpeedOptions.Count; i++)
        {
            if (SpeedOptions[i].Value == value)
            {
                return i;
            }
        }

        // Fall back to normal speed's slot rather than guessing.
        for (var i = 0; i < SpeedOptions.Count; i++)
        {
            if (SpeedOptions[i].Value == 1d)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Nudges the volume by a step; used by the Up/Down shortcuts.</summary>
    public void StepVolume(double delta)
    {
        VolumePercent = Math.Clamp(Math.Round(VolumePercent + delta), 0d, 100d);
        NotifyActivity();
    }

    private async Task SeekRelativeAsync(TimeSpan delta)
    {
        if (!_playback.IsOpen || !_playback.IsSeekable)
        {
            return;
        }

        var target = _playback.Position + delta;
        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }

        if (_playback.NaturalDuration is { } total && target > total)
        {
            target = total;
        }

        await _playback.SeekAsync(target);
        UpdateClockFromPlayback(force: true);
        NotifyActivity();
    }

    /// <summary>Index of the nearest playable lesson in the given direction, or -1.</summary>
    private int FindNeighbour(int offset)
    {
        if (_currentIndex < 0)
        {
            return -1;
        }

        // Skip documents and missing files so prev/next stay useful.
        for (var i = _currentIndex + offset; i >= 0 && i < Playlist.Count; i += offset)
        {
            if (Playlist[i].IsPlayable && Playlist[i].IsOnline)
            {
                return i;
            }
        }

        return -1;
    }

    private async Task GoToNeighbourAsync(int offset, bool announceEdge, string reason)
    {
        var index = FindNeighbour(offset);
        if (index < 0)
        {
            if (announceEdge)
            {
                _notifications.Show(offset > 0 ? "That was the last video." : "That was the first video.");
            }

            return;
        }

        NotifyActivity();
        await LoadAssetAsync(Playlist[index].AssetId, isFirstLoad: false, reason, CancellationToken.None);
    }

    // -------------------------------- scrubbing --------------------------------

    /// <summary>Called by the view when the user grabs the scrubber.</summary>
    public void BeginScrub()
    {
        IsScrubbing = true;
        AreControlsVisible = true;
        _controlsTimer.Stop();
    }

    /// <summary>Called by the view when the scrubber is released, at its final value.</summary>
    public async Task EndScrubAsync()
    {
        IsScrubbing = false;

        if (_playback.IsOpen && _playback.IsSeekable)
        {
            await _playback.SeekAsync(TimeSpan.FromSeconds(PositionSeconds));
            UpdateClockFromPlayback(force: true);
        }

        RestartControlsTimer();
    }

    // ------------------------------ control fade -------------------------------

    /// <summary>Called by the view on mouse movement to keep the controls awake.</summary>
    public void NotifyActivity()
    {
        AreControlsVisible = true;
        RestartControlsTimer();
    }

    private void RestartControlsTimer()
    {
        _controlsTimer.Stop();

        // Controls only fade while something is actually playing.
        if (IsPlaying && !IsScrubbing)
        {
            _controlsTimer.Start();
        }
    }

    private void OnControlsTimerTick(object? sender, EventArgs e)
    {
        _controlsTimer.Stop();
        if (IsPlaying && !IsScrubbing)
        {
            AreControlsVisible = false;
        }
    }
    // --------------------------------- clock -----------------------------------

    private void OnClockTick(object? sender, EventArgs e) => UpdateClockFromPlayback(force: false);

    private void UpdateClockFromPlayback(bool force)
    {
        if (_isDisposed || _isLeaving || _isSwitchingAsset)
        {
            return;
        }

        if (_playback.NaturalDuration is { } duration && duration > TimeSpan.Zero)
        {
            var totalSeconds = duration.TotalSeconds;
            if (Math.Abs(DurationSeconds - totalSeconds) > 0.25d)
            {
                DurationSeconds = totalSeconds;
                DurationText = FormatTime(duration);
            }
        }

        BufferingProgress = _playback.BufferingProgress;
        IsSeekable = !_playback.IsOpen || _playback.IsSeekable;

        if (IsScrubbing && !force)
        {
            return;
        }

        var position = _playback.Position;

        // Writing the clock into the scrubber must not be mistaken for the user seeking.
        _isClockWritingPosition = true;
        try
        {
            PositionSeconds = position.TotalSeconds;
        }
        finally
        {
            _isClockWritingPosition = false;
        }

        PositionText = FormatTime(position);

        // Keep the button in step with FFME's own state (end of stream, decode stall).
        if (_playback.IsOpen && IsPlaying != _playback.IsPlaying)
        {
            SetPlayingState(_playback.IsPlaying);
        }

        MaybeRecordCompletion(position);
    }

    // ------------------------------- completion --------------------------------

    private void MaybeRecordCompletion(TimeSpan position)
    {
        // While a switch is in flight the clock still belongs to the outgoing file, so a
        // reading here would be credited to the wrong lesson.
        if (_completionRecorded || _isSwitchingAsset || DurationSeconds <= 0d)
        {
            return;
        }

        if (position.TotalSeconds / DurationSeconds < CompletionThreshold)
        {
            return;
        }

        _completionRecorded = true;
        _ = MarkCompleteAsync();
    }

    private async Task MarkCompleteAsync()
    {
        var asset = _asset;
        if (asset is null)
        {
            return;
        }

        try
        {
            await SaveProgressAsync(CancellationToken.None, markCompleted: true);

            var item = Playlist.FirstOrDefault(i => i.AssetId == asset.Id);
            if (item is not null)
            {
                item.IsCompleted = true;
            }

            _notifications.Show("Marked as complete.");
            _logger.LogDebug("Asset {AssetId} marked complete.", asset.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not mark asset {AssetId} complete.", asset.Id);
        }
    }

    // --------------------------------- progress --------------------------------

    private async void OnAutoSaveTick(object? sender, EventArgs e)
    {
        try
        {
            await SaveProgressAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Periodic progress save failed.");
        }
    }

    private async Task SaveProgressAsync(CancellationToken cancellationToken, bool markCompleted = false)
    {
        var asset = _asset;
        if (asset is null || !_playback.IsOpen)
        {
            return;
        }

        var seconds = _playback.Position.TotalSeconds;
        if (double.IsNaN(seconds) || seconds < 0d)
        {
            seconds = 0d;
        }

        try
        {
            await _database.ExecuteAsync(async (context, token) =>
            {
                var row = await context.Progresses
                    .FirstOrDefaultAsync(p => p.AssetId == asset.Id, token)
                    .ConfigureAwait(false);

                if (row is null)
                {
                    row = new Progress { AssetId = asset.Id };
                    context.Progresses.Add(row);
                }

                row.WatchedSeconds = seconds;
                row.LastAccessedAt = DateTimeOffset.UtcNow;

                if (markCompleted)
                {
                    row.Completed = true;
                }
            }, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A failed resume write must never interrupt playback.
            _logger.LogError(ex, "Could not save progress for asset {AssetId}.", asset.Id);
        }
    }

    // ------------------------------ playback events ----------------------------

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        if (_isLeaving)
        {
            return;
        }

        if (_playback.NaturalDuration is { } duration && duration > TimeSpan.Zero)
        {
            DurationSeconds = duration.TotalSeconds;
            DurationText = FormatTime(duration);
        }

        HasSubtitles = _playback.HasSubtitles;
        IsSeekable = _playback.IsSeekable;

        if (_playback.VideoSize is { } size)
        {
            ResolutionBadge = BadgeForSize(size.Width, size.Height);
        }

        if (!_resumeApplied)
        {
            _resumeApplied = true;
            _ = ApplyResumeAsync();
        }
    }

    private async Task ApplyResumeAsync()
    {
        var resume = _resumePosition;
        if (resume <= MinimumResumePosition || !_playback.IsSeekable)
        {
            return;
        }

        if (_playback.NaturalDuration is { } duration && resume >= duration - ResumeEndGuard)
        {
            return;
        }

        try
        {
            await _playback.SeekAsync(resume);
            UpdateClockFromPlayback(force: true);
            _notifications.Show($"Resumed at {FormatTime(resume)}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not resume asset {AssetId} at {Position}.", _asset?.Id, resume);
        }
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        var position = _playback.Position;
        var duration = _playback.NaturalDuration;
        var elapsed = _playbackStartedAtUtc == DateTime.MaxValue
            ? TimeSpan.Zero
            : DateTime.UtcNow - _playbackStartedAtUtc;

        _logger.LogDebug(
            "MediaEnded for asset {AssetId} at {Position} of {Duration} after {Elapsed} (leaving={Leaving}, switching={Switching}).",
            _asset?.Id,
            position,
            duration,
            elapsed,
            _isLeaving,
            _isSwitchingAsset);

        if (_isLeaving || _isSwitchingAsset)
        {
            return;
        }

        // FFME also raises this while a container is torn down, which would otherwise chain
        // one autoplay into the next through the whole course. Only trust the event when the
        // clock really is at the end of a lesson that has been playing for a moment.
        var reachedEnd = duration is { } total
            && total > EndOfStreamTolerance
            && position >= total - EndOfStreamTolerance;
        var playedLongEnough = elapsed >= MinimumPlayTimeBeforeAutoAdvance;

        if (!reachedEnd || !playedLongEnough)
        {
            _logger.LogDebug(
                "Ignoring end-of-stream for asset {AssetId} (reachedEnd={ReachedEnd}, playedLongEnough={PlayedLongEnough}).",
                _asset?.Id,
                reachedEnd,
                playedLongEnough);
            return;
        }

        SetPlayingState(false);
        _autoSaveTimer.Stop();
        AreControlsVisible = true;
        _controlsTimer.Stop();

        // Never open the next file from inside FFME's own callback: the switch has to run
        // after this event has unwound, or two commands collide on the same container.
        _ = Application.Current?.Dispatcher.InvokeAsync(
            async () => await HandleMediaEndedAsync(),
            DispatcherPriority.Background);
    }

    private async Task HandleMediaEndedAsync()
    {
        try
        {
            if (!_completionRecorded)
            {
                _completionRecorded = true;
                await MarkCompleteAsync();
            }

            // A short pause so the "complete" toast is readable before the next lesson starts.
            await Task.Delay(TimeSpan.FromSeconds(1.5));

            if (!_isLeaving && !_isSwitchingAsset)
            {
                await GoToNeighbourAsync(1, announceEdge: false, "autoplay");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Autoplay after media end failed.");
        }
    }

    private void OnMediaFailed(object? sender, Exception error)
    {
        SetPlayingState(false);
        _clockTimer.Stop();
        _autoSaveTimer.Stop();
        AreControlsVisible = true;

        _logger.LogError(error, "Playback failed for asset {AssetId}.", _asset?.Id);

        if (!_isLeaving)
        {
            _notifications.Show("Playback issue - this file could not be decoded.");
        }
    }

    private void DetachPlaybackEvents()
    {
        if (_playbackEventsDetached)
        {
            return;
        }

        _playbackEventsDetached = true;

        _playback.MediaOpened -= OnMediaOpened;
        _playback.MediaEnded -= OnMediaEnded;
        _playback.MediaFailed -= OnMediaFailed;
    }
    // ------------------------------ property hooks -----------------------------

    private void SetPlayingState(bool isPlaying)
    {
        IsPlaying = isPlaying;
        PlayPauseIcon = isPlaying ? PackIconKind.Pause : PackIconKind.Play;
    }

    private void NotifyPlaylistBounds()
    {
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoToPrevious));
        OnPropertyChanged(nameof(CanGoToNext));
    }

    partial void OnVolumePercentChanged(double value)
    {
        _playback.Volume = value / 100d;

        // Dragging to zero mutes, dragging back up unmutes: the two controls stay consistent.
        if (value <= 0d && !IsMuted)
        {
            IsMuted = true;
        }
        else if (value > 0d && IsMuted)
        {
            IsMuted = false;
        }

        UpdateVolumeIcon();
    }

    partial void OnIsMutedChanged(bool value)
    {
        _playback.IsMuted = value;
        UpdateVolumeIcon();
    }

    partial void OnSelectedSpeedChanged(PlaybackSpeedOption value) => _playback.SpeedRatio = value.Value;

    partial void OnPositionSecondsChanged(double value)
    {
        // A change that did not come from the clock and is not part of a drag means the user
        // moved the scrubber directly - keyboard, or a click on the rail - so honour it.
        if (_isClockWritingPosition || IsScrubbing || _isLeaving || !_playback.IsOpen)
        {
            return;
        }

        var target = TimeSpan.FromSeconds(value);
        if ((target - _playback.Position).Duration() < ScrubberSeekEpsilon)
        {
            return;
        }

        _ = SeekFromScrubberAsync(target);
    }

    private async Task SeekFromScrubberAsync(TimeSpan target)
    {
        try
        {
            await _playback.SeekAsync(target);
            UpdateClockFromPlayback(force: true);
            NotifyActivity();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scrubber seek to {Target} failed.", target);
        }
    }

    partial void OnAreSubtitlesEnabledChanged(bool value) => _playback.AreSubtitlesEnabled = value;

    partial void OnIsPlayingChanged(bool value)
    {
        if (value)
        {
            RestartControlsTimer();
        }
        else
        {
            // Paused chrome stays put so the user can find the buttons.
            _controlsTimer.Stop();
            AreControlsVisible = true;
        }
    }

    private void UpdateVolumeIcon()
    {
        VolumeIcon = IsMuted || VolumePercent <= 0d
            ? PackIconKind.VolumeOff
            : VolumePercent < 40d
                ? PackIconKind.VolumeMedium
                : PackIconKind.VolumeHigh;
    }

    // -------------------------------- formatting -------------------------------

    private static string BadgeForResolution(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution))
        {
            return string.Empty;
        }

        var parts = resolution.Split('x', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], out var width)
            && int.TryParse(parts[1], out var height)
                ? BadgeForSize(width, height)
                : string.Empty;
    }

    private static string BadgeForSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return string.Empty;
        }

        // Judge by the short side so portrait clips are labelled like landscape ones.
        return Math.Min(width, height) switch
        {
            >= 2160 => "4K",
            >= 1440 => "1440p",
            >= 1080 => "1080p",
            >= 720 => "720p",
            _ => "SD",
        };
    }

    internal static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}"
            : $"{value.Minutes}:{value.Seconds:D2}";
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        _clockTimer.Tick -= OnClockTick;
        _clockTimer.Stop();
        _autoSaveTimer.Tick -= OnAutoSaveTick;
        _autoSaveTimer.Stop();
        _controlsTimer.Tick -= OnControlsTimerTick;
        _controlsTimer.Stop();

        DetachPlaybackEvents();

        GC.SuppressFinalize(this);
    }

    /// <summary>Flat projection used to build the playlist without loading whole entities.</summary>
    internal sealed class PlaylistRow
    {
        public int Id { get; init; }

        public string Title { get; init; } = string.Empty;

        public AssetType Type { get; init; }

        public TimeSpan? Duration { get; init; }

        public bool IsOnline { get; init; }

        public bool Completed { get; init; }
    }

    /// <summary>One row in the collapsible course playlist.</summary>
    public sealed partial class PlaylistItemViewModel : ObservableObject
    {
        internal PlaylistItemViewModel(PlaylistRow row)
        {
            AssetId = row.Id;
            Title = row.Title;
            Type = row.Type;
            IsOnline = row.IsOnline;
            DurationText = row.Duration is { } d ? FormatTime(d) : string.Empty;
            Icon = row.Type switch
            {
                AssetType.Video => PackIconKind.PlayCircleOutline,
                AssetType.Pdf => PackIconKind.FilePdfBox,
                AssetType.Docx => PackIconKind.FileWordBox,
                AssetType.Text => PackIconKind.FileDocumentOutline,
                _ => PackIconKind.FileOutline,
            };
            _isCompleted = row.Completed;
        }

        public int AssetId { get; }

        public string Title { get; }

        public AssetType Type { get; }

        public bool IsOnline { get; }

        public string DurationText { get; }

        public PackIconKind Icon { get; }

        /// <summary>Only videos play here; documents get their own viewers in a later phase.</summary>
        public bool IsPlayable => Type == AssetType.Video;

        [ObservableProperty]
        private bool _isCompleted;

        [ObservableProperty]
        private bool _isCurrent;
    }
}

/// <summary>A selectable playback rate.</summary>
public sealed class PlaybackSpeedOption
{
    public PlaybackSpeedOption(double value)
    {
        Value = value;
        Label = value == 1d ? "1x" : $"{value:0.##}x";
    }

    public double Value { get; }

    public string Label { get; }

    public override string ToString() => Label;
}