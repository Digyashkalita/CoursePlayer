using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
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
    /// Fractions of the running time sampled when choosing a cover frame. Spread across the
    /// body of the lesson: the opening seconds are usually a black fade or a title card, and
    /// the last moments are usually an outro.
    /// </summary>
    private static readonly double[] CandidateFractions = [0.12, 0.30, 0.45, 0.62, 0.80];

    /// <summary>
    /// Sampling a spread of frames only pays off once there is something to spread over.
    /// Below this, one frame from the usual seek point is as good as it gets.
    /// </summary>
    private static readonly TimeSpan MinimumDurationForSampling = TimeSpan.FromSeconds(12);

    /// <summary>
    /// A frame this bright overall is a blown-out white slide, which tells the viewer nothing.
    /// Such a frame is only used when nothing better was found.
    /// </summary>
    private const double MaximumUsefulLuma = 232d;

    /// <summary>
    /// A sample at or below this luma counts as dead: a letterbox bar, a pillarbox bar, or
    /// black background baked into a screen recording.
    /// </summary>
    private const double DeadLumaThreshold = 12d;

    /// <summary>
    /// A frame must fill at least this much of its canvas with non-dead pixels. Screen
    /// recordings routinely centre a narrow window on black, and such a frame scores high on
    /// raw contrast while looking like a rendering fault on a card.
    /// </summary>
    private const double MinimumCoverage = 0.6d;

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
    /// <remarks>
    /// A single fixed seek is unreliable: lessons routinely open on a black fade or a title
    /// slide, which produced covers that looked broken. Several frames are sampled in one
    /// ffmpeg pass and the most representative one wins.
    /// </remarks>
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

        // The database leaves Duration null until the metadata probe runs, and covers are
        // often generated first, so ask ffprobe rather than guessing at a fixed offset.
        var known = duration is { } d && d > TimeSpan.Zero
            ? d
            : await ProbeDurationAsync(videoPath, cancellationToken).ConfigureAwait(false);

        if (known is { } length && length >= MinimumDurationForSampling
            && await TrySampledFrameAsync(executable, videoPath, targetPath, length, cancellationToken)
                .ConfigureAwait(false))
        {
            return true;
        }

        var seek = known is { } total && total > TimeSpan.Zero
            ? TimeSpan.FromSeconds(total.TotalSeconds * SeekFraction)
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
    /// Extracts <see cref="CandidateFractions"/> in one ffmpeg invocation, scores each, and
    /// promotes the winner. One process for all candidates keeps this about as cheap as the
    /// single-frame path — the cost is dominated by process startup, not by decoding.
    /// </summary>
    private async Task<bool> TrySampledFrameAsync(
        string executable,
        string videoPath,
        string targetPath,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var workspace = Path.Combine(
            Path.GetDirectoryName(targetPath) ?? Path.GetTempPath(),
            "candidates");

        try
        {
            Directory.CreateDirectory(workspace);

            var candidates = new List<string>(CandidateFractions.Length);
            var arguments = new StringBuilder("-hide_banner -loglevel error -nostdin -y");

            foreach (var fraction in CandidateFractions)
            {
                var at = TimeSpan.FromSeconds(duration.TotalSeconds * fraction);
                arguments.Append(" -ss ")
                    .Append(at.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture))
                    .Append(" -i \"")
                    .Append(videoPath)
                    .Append('"');
            }

            for (var i = 0; i < CandidateFractions.Length; i++)
            {
                var candidate = Path.Combine(workspace, $"c{i}.jpg");
                candidates.Add(candidate);

                arguments.Append(" -map ")
                    .Append(i)
                    .Append(":v -frames:v 1 -vf \"")
                    .Append(ScaleFilter)
                    .Append("\" -q:v 4 \"")
                    .Append(candidate)
                    .Append('"');
            }

            // A partial result is fine: as long as one candidate landed, a cover can be picked.
            await RunFFmpegAsync(executable, arguments.ToString(), cancellationToken).ConfigureAwait(false);

            var best = PickBestCandidate(candidates);
            if (best is null)
            {
                return false;
            }

            File.Copy(best, targetPath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Frame sampling failed for {Path}; falling back to a single seek.", videoPath);
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(workspace))
                {
                    Directory.Delete(workspace, recursive: true);
                }
            }
            catch
            {
                // Leftover candidates are harmless; they sit inside the asset's own cache folder.
            }
        }
    }

    /// <summary>
    /// Picks the most representative candidate. Frames that only fill a sliver of their canvas
    /// are rejected first — a screen recording often centres a narrow window on black, and such
    /// a frame reads as a rendering fault on a card no matter how much contrast it carries.
    /// Among frames that do fill the canvas, the one with the most visual detail wins. Falls
    /// back to the best of a bad set rather than giving up, since any frame beats no cover.
    /// </summary>
    private static string? PickBestCandidate(IReadOnlyList<string> candidates)
    {
        string? best = null;
        var bestScore = double.MinValue;

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)
                || !TryMeasure(candidate, out var luma, out var detail, out var coverage))
            {
                continue;
            }

            // Detail carries the ranking. Deliberately no lower bound on luma: a lesson
            // recorded against a dark IDE or editor is legitimately dark, and penalising that
            // would hand the cover to whichever frame happened to flash a white slide.
            var score = detail;

            if (coverage < MinimumCoverage || luma > MaximumUsefulLuma)
            {
                score -= 1000d;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Measures one candidate over a 32x18 reduction: mean luma, the spread of its live pixels,
    /// and how much of the canvas is live at all. Small enough that reading pixels one at a
    /// time costs nothing, and enough signal to separate a fade from a real shot and a
    /// full-frame shot from a narrow window floating on black.
    /// </summary>
    private static bool TryMeasure(
        string imagePath,
        out double luma,
        out double detail,
        out double coverage)
    {
        luma = 0d;
        detail = 0d;
        coverage = 0d;

        try
        {
            // Read through memory so no file handle is left on the candidate.
            var bytes = File.ReadAllBytes(imagePath);
            using var stream = new MemoryStream(bytes, writable: false);
            using var image = Image.FromStream(stream);

            const int width = 32;
            const int height = 18;

            using var reduced = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(reduced))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.DrawImage(image, 0, 0, width, height);
            }

            var samples = new double[width * height];
            var index = 0;

            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var pixel = reduced.GetPixel(x, y);
                    samples[index++] = (0.2126 * pixel.R) + (0.7152 * pixel.G) + (0.0722 * pixel.B);
                }
            }

            luma = samples.Average();

            // Letterbox bars and black backdrop are excluded from the detail figure, otherwise
            // the bar-to-picture edge alone would score as enormous contrast.
            var live = samples.Where(sample => sample > DeadLumaThreshold).ToArray();
            coverage = (double)live.Length / samples.Length;

            if (live.Length < 8)
            {
                // Essentially an empty frame: no meaningful detail to report.
                return true;
            }

            var mean = live.Average();
            var variance = live.Sum(sample => (sample - mean) * (sample - mean)) / live.Length;
            detail = Math.Sqrt(variance);
            return true;
        }
        catch (Exception)
        {
            // Unreadable or half-written candidate: simply not a contender.
            return false;
        }
    }

    /// <summary>
    /// Asks ffprobe how long the file is. Returns null when ffprobe is missing or the file
    /// has no usable duration, which sends the caller to its fixed-offset fallback.
    /// </summary>
    private async Task<TimeSpan?> ProbeDurationAsync(string videoPath, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(_paths.FFmpegDirectory, "ffprobe.exe");
        if (!File.Exists(executable))
        {
            return null;
        }

        var arguments = "-v error -show_entries format=duration " +
                        $"-of default=nw=1:nk=1 \"{videoPath}\"";

        var (ok, output) = await RunProcessAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            return null;
        }

        var text = output.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0
            && !double.IsInfinity(seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }

    /// <summary>
    /// scale with force_original_aspect_ratio=decrease then pad centres the frame inside a
    /// fixed 16:9 canvas, so a portrait or 4:3 lesson is letterboxed instead of stretched.
    /// </summary>
    private static string ScaleFilter =>
        $"scale={ThumbnailWidth}:{ThumbnailHeight}:force_original_aspect_ratio=decrease," +
        $"pad={ThumbnailWidth}:{ThumbnailHeight}:(ow-iw)/2:(oh-ih)/2:color=black";

    /// <summary>
    /// Single-frame extraction. -ss before -i seeks by keyframe, which is far cheaper than
    /// decoding up to the mark.
    /// </summary>
    private static string BuildFrameArguments(string videoPath, string targetPath, TimeSpan? seek)
    {
        var seekArgument = seek is { } value
            ? $"-ss {value.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)} "
            : string.Empty;

        return "-hide_banner -loglevel error -nostdin -y " +
               seekArgument +
               $"-i \"{videoPath}\" -frames:v 1 " +
               $"-vf \"{ScaleFilter}\" " +
               $"-q:v 4 \"{targetPath}\"";
    }

    private async Task<bool> RunFFmpegAsync(
        string executable,
        string arguments,
        CancellationToken cancellationToken)
    {
        var (ok, _) = await RunProcessAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
        return ok;
    }

    private async Task<(bool Ok, string Output)> RunProcessAsync(
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
            return (false, string.Empty);
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
            _logger.LogWarning("{Tool} timed out generating a cover; killing it.", Path.GetFileName(executable));
            TryKill(process);
            return (false, string.Empty);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var error = await stderr.ConfigureAwait(false);
        var output = await stdout.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            _logger.LogDebug(
                "{Tool} exited {ExitCode} generating a cover: {Error}",
                Path.GetFileName(executable),
                process.ExitCode,
                error.Trim());
            return (false, output);
        }

        return (true, output);
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
