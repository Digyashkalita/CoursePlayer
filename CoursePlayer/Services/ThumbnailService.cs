using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using CoursePlayer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PdfiumViewer.Core;
using PdfiumViewer.Enums;

namespace CoursePlayer.Services;

/// <summary>
/// Produces 16:9 cover images: a real frame for videos, a rendered first page for PDFs, and
/// nothing at all for formats that cannot be rasterised (the UI falls back to a type icon).
/// Generation is best-effort and never throws — a course full of unreadable files still
/// imports and still opens.
/// </summary>
/// <remarks>
/// Covers are addressed by id, not by a database column: an asset's cover always lives at
/// <c>Assets\Thumbnails\{assetId}\cover.jpg</c> and a course's at
/// <c>Assets\CourseThumbnails\{courseId}.jpg</c>. Nothing needs migrating, and deleting the
/// cache simply makes the next load regenerate.
/// </remarks>
public interface IThumbnailService
{
    /// <summary>
    /// Creates the cover for one asset unless a usable file already exists. Returns the
    /// absolute path to the image, or null when none could be produced.
    /// </summary>
    Task<string?> EnsureAssetThumbnailAsync(
        int assetId,
        string filePath,
        AssetType type,
        TimeSpan? duration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The cover for an asset if one has already been generated, else null. Cheap — this
    /// only tests for the file, so it is safe to call while building view models.
    /// </summary>
    string? GetAssetThumbnailPath(int assetId);

    /// <summary>
    /// The cover for a course: the user-supplied image when set, otherwise the generated
    /// one. Null when neither exists yet.
    /// </summary>
    string? GetCourseThumbnailPath(int courseId, string? userSuppliedPath = null);

    /// <summary>
    /// Generates every missing cover for one course, reporting each asset as it completes so
    /// the grid can fill in progressively. The course cover is derived from the first asset
    /// that yields an image.
    /// </summary>
    Task GenerateForCourseAsync(
        int courseId,
        Action<int, string>? onAssetThumbnail = null,
        Action<string>? onCourseThumbnail = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces just the course cover, stopping at the first asset that yields an image.
    /// Home uses this so a large library does not have to rasterise every lesson before the
    /// first card gains its artwork.
    /// </summary>
    Task<string?> EnsureCourseThumbnailAsync(int courseId, CancellationToken cancellationToken = default);

    /// <summary>Removes the generated covers for one asset.</summary>
    void DeleteAssetThumbnails(int assetId);
}

/// <inheritdoc cref="IThumbnailService"/>
public sealed class ThumbnailService : IThumbnailService
{
    /// <summary>Cover size in pixels — 16:9, matching the card and playlist artwork.</summary>
    private const int ThumbnailWidth = 320;

    private const int ThumbnailHeight = 180;

    /// <summary>File name for the single cover frame inside an asset's folder.</summary>
    private const string CoverFileName = "cover.jpg";

    /// <summary>
    /// How long to let FFmpeg run before giving up. A cover is a nicety; a hung child
    /// process blocking an import is not.
    /// </summary>
    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Where to grab the frame. Ten percent in skips title cards and black leader without
    /// landing deep inside a long lesson.
    /// </summary>
    private const double SeekFraction = 0.1;

    /// <summary>Seek used when the duration has not been probed yet.</summary>
    private static readonly TimeSpan FallbackSeek = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Only one cover is produced at a time. Each video cover spawns an ffmpeg process, and
    /// a 200-lesson course would otherwise try to start 200 of them at once.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly IAppPaths _paths;
    private readonly IDatabaseWriter _database;
    private readonly ILogger<ThumbnailService> _logger;

    public ThumbnailService(
        IAppPaths paths,
        IDatabaseWriter database,
        ILogger<ThumbnailService> logger)
    {
        _paths = paths;
        _database = database;
        _logger = logger;
    }

    public string? GetAssetThumbnailPath(int assetId)
    {
        try
        {
            var cover = Path.Combine(_paths.GetAssetThumbnailDirectory(assetId), CoverFileName);
            return File.Exists(cover) ? cover : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public string? GetCourseThumbnailPath(int courseId, string? userSuppliedPath = null)
    {
        try
        {
            // A cover the user chose always wins over the generated one.
            if (!string.IsNullOrWhiteSpace(userSuppliedPath) && File.Exists(userSuppliedPath))
            {
                return userSuppliedPath;
            }

            var generated = CourseCoverPath(courseId);
            return File.Exists(generated) ? generated : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public async Task<string?> EnsureAssetThumbnailAsync(
        int assetId,
        string filePath,
        AssetType type,
        TimeSpan? duration,
        CancellationToken cancellationToken = default)
    {
        var existing = GetAssetThumbnailPath(assetId);
        if (existing is not null)
        {
            return existing;
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        // These are the only two kinds we can rasterise; everything else shows a type icon.
        if (type is not (AssetType.Video or AssetType.Pdf))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have produced it while we waited for the gate.
            existing = GetAssetThumbnailPath(assetId);
            if (existing is not null)
            {
                return existing;
            }

            var directory = _paths.GetAssetThumbnailDirectory(assetId);
            Directory.CreateDirectory(directory);

            var target = Path.Combine(directory, CoverFileName);

            var produced = type == AssetType.Video
                ? await ExtractVideoFrameAsync(filePath, target, duration, cancellationToken).ConfigureAwait(false)
                : await RenderPdfCoverAsync(filePath, target, cancellationToken).ConfigureAwait(false);

            if (produced && File.Exists(target))
            {
                _logger.LogDebug("Generated cover for asset {AssetId}.", assetId);
                return target;
            }

            _logger.LogDebug("No cover could be generated for asset {AssetId} ({Type}).", assetId, type);
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cover generation failed for asset {AssetId}.", assetId);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task GenerateForCourseAsync(
        int courseId,
        Action<int, string>? onAssetThumbnail = null,
        Action<string>? onCourseThumbnail = null,
        CancellationToken cancellationToken = default)
    {
        var requests = await ListCoverRequestsAsync(courseId, cancellationToken).ConfigureAwait(false);
        if (requests.Count == 0)
        {
            return;
        }

        var generated = 0;
        string? firstCover = null;

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cover = await EnsureAssetThumbnailAsync(
                request.AssetId,
                request.FilePath,
                request.Type,
                request.Duration,
                cancellationToken).ConfigureAwait(false);

            if (cover is null)
            {
                continue;
            }

            generated++;
            firstCover ??= cover;
            onAssetThumbnail?.Invoke(request.AssetId, cover);
        }

        if (generated > 0)
        {
            _logger.LogInformation(
                "Course {CourseId}: {Count} asset cover(s) available.",
                courseId,
                generated);
        }

        if (firstCover is not null)
        {
            var courseCover = CopyCourseCover(courseId, firstCover);
            if (courseCover is not null)
            {
                onCourseThumbnail?.Invoke(courseCover);
            }
        }
    }

    public async Task<string?> EnsureCourseThumbnailAsync(
        int courseId,
        CancellationToken cancellationToken = default)
    {
        var existing = GetCourseThumbnailPath(courseId);
        if (existing is not null)
        {
            return existing;
        }

        var requests = await ListCoverRequestsAsync(courseId, cancellationToken).ConfigureAwait(false);

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cover = await EnsureAssetThumbnailAsync(
                request.AssetId,
                request.FilePath,
                request.Type,
                request.Duration,
                cancellationToken).ConfigureAwait(false);

            // The first asset that rasterises becomes the course cover; the rest wait until
            // the user actually opens the course.
            if (cover is not null)
            {
                return CopyCourseCover(courseId, cover);
            }
        }

        return null;
    }

    /// <summary>
    /// The rasterisable assets of one course, videos first so a course cover comes from a
    /// real frame rather than a document page when both are present.
    /// </summary>
    private async Task<List<AssetCoverRequest>> ListCoverRequestsAsync(
        int courseId,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await _database.QueryAsync(
                (context, token) => context.Assets
                    .AsNoTracking()
                    .Where(a => a.CourseId == courseId
                                && a.IsOnline
                                && (a.Type == AssetType.Video || a.Type == AssetType.Pdf))
                    .OrderBy(a => a.OrderIndex)
                    .Select(a => new AssetCoverRequest(a.Id, a.FilePath, a.Type, a.Duration))
                    .ToListAsync(token),
                cancellationToken).ConfigureAwait(false);

            return rows
                .OrderBy(r => r.Type == AssetType.Video ? 0 : 1)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list assets for cover generation (course {CourseId}).", courseId);
            return [];
        }
    }

    public void DeleteAssetThumbnails(int assetId)
    {
        try
        {
            var directory = _paths.GetAssetThumbnailDirectory(assetId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete covers for asset {AssetId}.", assetId);
        }
    }

    private string CourseCoverPath(int courseId) =>
        Path.Combine(_paths.CourseThumbnailsDirectory, $"{courseId}.jpg");

    /// <summary>
    /// Promotes an asset cover to the course cover by copying it. A copy rather than a
    /// reference keeps the card working after that one asset is removed or re-cached.
    /// </summary>
    private string? CopyCourseCover(int courseId, string assetCover)
    {
        try
        {
            var target = CourseCoverPath(courseId);
            if (File.Exists(target))
            {
                return target;
            }

            Directory.CreateDirectory(_paths.CourseThumbnailsDirectory);
            File.Copy(assetCover, target, overwrite: true);
            _logger.LogDebug("Course {CourseId} cover set from {Source}.", courseId, assetCover);
            return target;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not set the cover for course {CourseId}.", courseId);
            return null;
        }
    }

    /// <summary>
    /// Grabs one frame with the bundled ffmpeg.exe. The in-process FFME libraries cannot do
    /// this without a live MediaElement, and building one per asset during an import is far
    /// too expensive, so this shells out instead.
    /// </summary>
    private async Task<bool> ExtractVideoFrameAsync(
        string videoPath,
        string targetPath,
        TimeSpan? duration,
        CancellationToken cancellationToken)
    {
        var executable = Path.Combine(_paths.FFmpegDirectory, "ffmpeg.exe");
        if (!File.Exists(executable))
        {
            _logger.LogDebug("ffmpeg.exe is not present at {Path}; skipping video covers.", executable);
            return false;
        }

        var seek = duration is { } known && known > TimeSpan.Zero
            ? TimeSpan.FromSeconds(known.TotalSeconds * SeekFraction)
            : FallbackSeek;

        var ran = await RunFFmpegAsync(
            executable,
            BuildFrameArguments(videoPath, targetPath, seek),
            cancellationToken).ConfigureAwait(false);

        // Seeking past the last keyframe of a very short clip yields no frame at all. Retry
        // once from the start before giving up.
        if (!ran || !File.Exists(targetPath))
        {
            ran = await RunFFmpegAsync(
                executable,
                BuildFrameArguments(videoPath, targetPath, seek: null),
                cancellationToken).ConfigureAwait(false);
        }

        return ran && File.Exists(targetPath);
    }

    /// <summary>
    /// scale with force_original_aspect_ratio=decrease then pad centres the frame inside a
    /// fixed 16:9 canvas, so a portrait or 4:3 lesson is letterboxed instead of stretched.
    /// -ss before -i seeks by keyframe, which is far cheaper than decoding up to the mark.
    /// </summary>
    private static string BuildFrameArguments(string videoPath, string targetPath, TimeSpan? seek)
    {
        var seekArgument = seek is { } value
            ? $"-ss {value.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)} "
            : string.Empty;

        return "-hide_banner -loglevel error -nostdin -y " +
               seekArgument +
               $"-i \"{videoPath}\" -frames:v 1 " +
               $"-vf \"scale={ThumbnailWidth}:{ThumbnailHeight}:force_original_aspect_ratio=decrease," +
               $"pad={ThumbnailWidth}:{ThumbnailHeight}:(ow-iw)/2:(oh-ih)/2:color=black\" " +
               $"-q:v 4 \"{targetPath}\"";
    }

    private async Task<bool> RunFFmpegAsync(
        string executable,
        string arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _paths.FFmpegDirectory,
        };

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExtractTimeout);

        // Drain both pipes: a full stderr buffer deadlocks the child.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("ffmpeg timed out generating a cover; killing it.");
            TryKill(process);
            return false;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var error = await stderr.ConfigureAwait(false);
        await stdout.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            _logger.LogDebug(
                "ffmpeg exited {ExitCode} generating a cover: {Error}",
                process.ExitCode,
                error.Trim());
            return false;
        }

        return true;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already gone, which is the outcome we wanted.
        }
    }

    /// <summary>
    /// Rasterises page one with PDFium, letterboxed into the same 16:9 frame the video
    /// covers use so the grid stays uniform.
    /// </summary>
    private Task<bool> RenderPdfCoverAsync(string pdfPath, string targetPath, CancellationToken cancellationToken)
    {
        // PDFium is entirely synchronous and CPU-bound; keep it off the caller's thread.
        return Task.Run(
            () =>
            {
                try
                {
                    using var document = PdfDocument.Load(pdfPath);
                    if (document.PageCount < 1)
                    {
                        return false;
                    }

                    var page = document.Pages[0];
                    if (page.Width <= 0 || page.Height <= 0)
                    {
                        return false;
                    }

                    // Fit the page inside the frame rather than cropping it.
                    var scale = Math.Min(
                        ThumbnailWidth / page.Width,
                        ThumbnailHeight / page.Height);

                    var renderWidth = Math.Max(1, (int)Math.Round(page.Width * scale));
                    var renderHeight = Math.Max(1, (int)Math.Round(page.Height * scale));

                    using var rendered = page.Render(
                        renderWidth,
                        renderHeight,
                        dpiX: 96f,
                        dpiY: 96f,
                        PdfiumViewer.PdfRotation.Rotate0,
                        PdfRenderFlags.Annotations);

                    // Letterbox onto a fixed canvas: a portrait page keeps its shape.
                    using var canvas = new Bitmap(ThumbnailWidth, ThumbnailHeight);
                    using (var graphics = Graphics.FromImage(canvas))
                    {
                        graphics.Clear(Color.White);
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

                        var x = (ThumbnailWidth - renderWidth) / 2;
                        var y = (ThumbnailHeight - renderHeight) / 2;
                        graphics.DrawImage(rendered, x, y, renderWidth, renderHeight);
                    }

                    canvas.Save(targetPath, ImageFormat.Jpeg);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "PDFium could not render a cover for {Path}.", pdfPath);
                    return false;
                }
            },
            cancellationToken);
    }

    /// <summary>The minimum an asset needs for its cover to be generated.</summary>
    private sealed record AssetCoverRequest(int AssetId, string FilePath, AssetType Type, TimeSpan? Duration);
}
