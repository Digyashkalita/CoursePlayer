namespace CoursePlayer.Data;

/// <summary>
/// A single playable or readable file inside a course.
/// </summary>
public class Asset
{
    public int Id { get; set; }

    public int CourseId { get; set; }

    public Course Course { get; set; } = null!;

    public string Title { get; set; } = string.Empty;

    /// <summary>Absolute path on disk. Unique within a course, so re-imports dedupe.</summary>
    public string FilePath { get; set; } = string.Empty;

    public AssetType Type { get; set; }

    /// <summary>Position in the course playlist.</summary>
    public int OrderIndex { get; set; }

    /// <summary>
    /// The containing folder's path relative to the course root (e.g. "Section 1" or
    /// "Module 1/Part A"). Null for files that sit directly in the course's root folder.
    /// Preserves the imported folder hierarchy so the detail view can group by section.
    /// </summary>
    public string? Section { get; set; }

    /// <summary>Null for documents, or for videos whose metadata could not be probed.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Video codec name reported by FFmpeg, e.g. "h264".</summary>
    public string? Codec { get; set; }

    /// <summary>Video frame size as "WIDTHxHEIGHT", e.g. "1920x1080".</summary>
    public string? Resolution { get; set; }

    /// <summary>
    /// False once the file is known to be missing from disk. The asset stays in the
    /// course, greyed out, rather than being deleted.
    /// </summary>
    public bool IsOnline { get; set; } = true;

    public Progress? Progress { get; set; }
}
