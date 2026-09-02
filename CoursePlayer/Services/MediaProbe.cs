using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using Unosquare.FFME;

namespace CoursePlayer.Services;

/// <summary>
/// Reads duration, codec and resolution from a media file via FFmpeg. Never throws: a file
/// that will not probe (corrupt, wrong codec, offline) yields an empty result and the asset
/// keeps its null metadata.
/// </summary>
public interface IMediaProbe
{
    Task<ProbeResult> ProbeAsync(string filePath, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IMediaProbe"/>
public sealed class MediaProbe : IMediaProbe
{
    private readonly IFFmpegBootstrapper _ffmpeg;
    private readonly ILogger<MediaProbe> _logger;

    public MediaProbe(IFFmpegBootstrapper ffmpeg, ILogger<MediaProbe> logger)
    {
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    public Task<ProbeResult> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!_ffmpeg.IsAvailable)
        {
            return Task.FromResult(ProbeResult.Empty);
        }

        return Task.Run(() => Probe(filePath), cancellationToken);
    }

    private ProbeResult Probe(string filePath)
    {
        try
        {
            // Library.RetrieveMediaInfo opens the container, reads its headers, and disposes
            // it — no playback, no UI element. FFmpeg is already loaded at startup.
            var info = Library.RetrieveMediaInfo(filePath);

            // FFME reports an unknown container duration as TimeSpan.MinValue.
            TimeSpan? duration = info.Duration > TimeSpan.Zero ? info.Duration : null;

            string? codec = null;
            string? resolution = null;

            if (info.BestStreams.TryGetValue(AVMediaType.AVMEDIA_TYPE_VIDEO, out var video))
            {
                codec = string.IsNullOrWhiteSpace(video.CodecName) ? null : video.CodecName;

                if (video.PixelWidth > 0 && video.PixelHeight > 0)
                {
                    resolution = $"{video.PixelWidth}x{video.PixelHeight}";
                }
            }

            return new ProbeResult(duration, codec, resolution);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not probe media file {Path}.", filePath);
            return ProbeResult.Empty;
        }
    }
}
