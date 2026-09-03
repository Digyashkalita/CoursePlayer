using CoursePlayer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoursePlayer.Services;

/// <summary>
/// Entry point for ingestion. Both the sidebar button and the Home drop zone go through
/// here so the scan → wizard → persist → probe flow lives in one place.
/// </summary>
public interface IImportCoordinator
{
    /// <summary>Prompts for a folder, then imports it.</summary>
    Task StartFromFolderPickerAsync();

    /// <summary>Imports paths dropped onto the window (folders and/or individual files).</summary>
    Task StartFromPathsAsync(IReadOnlyList<string> paths);

    /// <summary>Raised on the thread that finished persistence, after courses are committed.</summary>
    event EventHandler? ImportCompleted;
}

/// <summary>
/// Orchestrates a full import: scan the paths, let the user review them in the wizard,
/// write the confirmed courses in one transaction, then enrich video metadata in the
/// background so the UI never waits on FFmpeg.
/// </summary>
public sealed class ImportCoordinator : IImportCoordinator, IDisposable
{
    // Persist probe results in small batches so a large course still shows progress and a
    // crash mid-enrichment only loses the current batch.
    private const int ProbeBatchSize = 16;

    private readonly IFilePickerService _filePicker;
    private readonly IFolderScanner _scanner;
    private readonly IImportWizardService _wizard;
    private readonly IMediaProbe _probe;
    private readonly IDatabaseWriter _database;
    private readonly IFFmpegBootstrapper _ffmpeg;
    private readonly IThumbnailService _thumbnails;
    private readonly INotificationService _notifications;
    private readonly ILogger<ImportCoordinator> _logger;

    private readonly CancellationTokenSource _shutdownCts = new();

    public ImportCoordinator(
        IFilePickerService filePicker,
        IFolderScanner scanner,
        IImportWizardService wizard,
        IMediaProbe probe,
        IDatabaseWriter database,
        IFFmpegBootstrapper ffmpeg,
        IThumbnailService thumbnails,
        INotificationService notifications,
        ILogger<ImportCoordinator> logger)
    {
        _filePicker = filePicker;
        _scanner = scanner;
        _wizard = wizard;
        _probe = probe;
        _database = database;
        _ffmpeg = ffmpeg;
        _thumbnails = thumbnails;
        _notifications = notifications;
        _logger = logger;
    }

    public event EventHandler? ImportCompleted;

    public Task StartFromFolderPickerAsync()
    {
        var folder = _filePicker.PickFolder("Choose a folder to import");
        return folder is null
            ? Task.CompletedTask
            : StartFromPathsAsync([folder]);
    }

    public async Task StartFromPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        if (paths.Any(IsNetworkPath))
        {
            _notifications.Show(
                "That's a network location — scanning speed may vary and files can go offline.");
        }

        try
        {
            var scan = await _scanner.ScanAsync(paths, _shutdownCts.Token).ConfigureAwait(false);
            if (scan.IsEmpty)
            {
                _notifications.Show("No videos or documents were found there.");
                return;
            }

            var confirmed = await _wizard.ShowAsync(scan).ConfigureAwait(false);
            if (confirmed is null || confirmed.Courses.Count == 0)
            {
                return; // Cancelled, or everything unticked.
            }

            var saved = await PersistAsync(confirmed, _shutdownCts.Token).ConfigureAwait(false);

            var courseWord = saved.Count == 1 ? "course" : "courses";
            _notifications.Show($"Imported {saved.Count} {courseWord}.");
            ImportCompleted?.Invoke(this, EventArgs.Empty);

            // Fire-and-forget: enrichment updates the DB and never blocks the import result.
            _ = EnrichInBackgroundAsync(saved, _shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            // App is shutting down; nothing to report.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import failed for {PathCount} path(s).", paths.Count);
            _notifications.Show("Import failed. See the log for details.");
        }
    }

    /// <summary>Writes the confirmed courses and their assets in one transaction.</summary>
    private Task<IReadOnlyList<Course>> PersistAsync(
        ImportWizardResult confirmed,
        CancellationToken cancellationToken) =>
        _database.ExecuteAsync<IReadOnlyList<Course>>(async (context, token) =>
        {
            var now = DateTimeOffset.Now;
            var courses = new List<Course>(confirmed.Courses.Count);

            foreach (var detected in confirmed.Courses)
            {
                var course = new Course
                {
                    Title = detected.Title,
                    FolderPath = detected.FolderPath,
                    ImportedAt = now,
                };

                // Guard against a file appearing twice in the scan; the unique
                // (CourseId, FilePath) index would otherwise reject the whole write.
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var asset in detected.Assets)
                {
                    if (!seen.Add(asset.FilePath))
                    {
                        continue;
                    }

                    course.Assets.Add(new Asset
                    {
                        Title = asset.Title,
                        FilePath = asset.FilePath,
                        Type = asset.Type,
                        OrderIndex = asset.OrderIndex,
                        Section = asset.Section,
                        IsOnline = true,
                    });
                }

                context.Courses.Add(course);
                courses.Add(course);
            }

            await context.SaveChangesAsync(token).ConfigureAwait(false);
            return courses;
        }, cancellationToken);

    /// <summary>Probes every video and writes back duration/codec/resolution in batches.</summary>
    private async Task EnrichInBackgroundAsync(IReadOnlyList<Course> courses, CancellationToken cancellationToken)
    {
        // Probing needs FFmpeg in-process; covers only need ffmpeg.exe and PDFium, so a
        // PDF-only course still gets artwork even when the shared libraries are missing.
        if (!_ffmpeg.IsAvailable)
        {
            _logger.LogInformation("Skipping metadata enrichment: FFmpeg is unavailable.");
            await GenerateCoversAsync(courses, cancellationToken).ConfigureAwait(false);
            return;
        }

        var videoAssetIds = courses
            .SelectMany(c => c.Assets)
            .Where(a => a.Type == AssetType.Video)
            .Select(a => a.Id)
            .ToList();

        if (videoAssetIds.Count == 0)
        {
            await GenerateCoversAsync(courses, cancellationToken).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("Enriching metadata for {Count} video(s).", videoAssetIds.Count);

        var enriched = 0;

        try
        {
            foreach (var batch in Chunk(videoAssetIds, ProbeBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Probe the batch first (no DB context held open across FFmpeg calls), then
                // apply all results in one short transaction.
                var probes = new List<(int AssetId, ProbeResult Result)>(batch.Count);
                foreach (var assetId in batch)
                {
                    var asset = courses
                        .SelectMany(c => c.Assets)
                        .First(a => a.Id == assetId);

                    var result = await _probe.ProbeAsync(asset.FilePath, cancellationToken).ConfigureAwait(false);
                    if (result.HasAny)
                    {
                        probes.Add((assetId, result));
                    }
                }

                if (probes.Count == 0)
                {
                    continue;
                }

                await _database.ExecuteAsync(async (context, token) =>
                {
                    var ids = probes.Select(p => p.AssetId).ToList();
                    var rows = await context.Assets
                        .Where(a => ids.Contains(a.Id))
                        .ToDictionaryAsync(a => a.Id, token)
                        .ConfigureAwait(false);

                    foreach (var (assetId, result) in probes)
                    {
                        if (!rows.TryGetValue(assetId, out var row))
                        {
                            continue;
                        }

                        row.Duration = result.Duration;
                        row.Codec = result.Codec;
                        row.Resolution = result.Resolution;
                    }
                }, cancellationToken).ConfigureAwait(false);

                enriched += probes.Count;
            }

            _logger.LogInformation("Metadata enrichment complete: {Count} video(s) updated.", enriched);
            _notifications.Show("Finished reading video details.");

            // Covers come last: durations are known by now, so each frame is grabbed at the
            // intended 10% mark rather than the blind 5-second fallback.
            await GenerateCoversAsync(courses, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-enrichment; whatever committed is fine.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metadata enrichment failed after {Count} video(s).", enriched);
        }
    }

    /// <summary>
    /// Generates the cover art for freshly imported courses so the grid is already populated
    /// the first time the user looks at it.
    /// </summary>
    private async Task GenerateCoversAsync(IReadOnlyList<Course> courses, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var course in courses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _thumbnails
                    .GenerateForCourseAsync(course.Id, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down; partial covers are fine, the rest generate on next view.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cover generation failed during import.");
        }
    }

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
        {
            yield return source.Skip(i).Take(size).ToList();
        }
    }

    private static bool IsNetworkPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal);

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
    }
}
