namespace CoursePlayer.Data;

/// <summary>
/// Per-asset playback/reading position. One row per asset, created lazily the first
/// time the asset is opened.
/// </summary>
public class Progress
{
    public int Id { get; set; }

    public int AssetId { get; set; }

    public Asset Asset { get; set; } = null!;

    public bool Completed { get; set; }

    /// <summary>Video resume point, in seconds. Written every 5s during playback.</summary>
    public double WatchedSeconds { get; set; }

    /// <summary>PDF resume point (1-based). Null for non-paged assets.</summary>
    public int? LastPage { get; set; }

    public DateTimeOffset LastAccessedAt { get; set; }
}
