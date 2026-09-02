using CoursePlayer.Data;

namespace CoursePlayer.Services;

/// <summary>
/// The immutable output of a folder scan: the courses the scanner proposes, each with its
/// assets. This is what the import wizard renders and edits before anything is written.
/// </summary>
public sealed record ScanResult(IReadOnlyList<DetectedCourse> Courses)
{
    public static readonly ScanResult Empty = new([]);

    public bool IsEmpty => Courses.Count == 0;
}

/// <summary>One proposed course: a folder (or a loose-file group) and the media inside it.</summary>
public sealed record DetectedCourse(
    string SuggestedTitle,
    string FolderPath,
    IReadOnlyList<DetectedAsset> Assets);

/// <summary>One proposed asset within a <see cref="DetectedCourse"/>.</summary>
public sealed record DetectedAsset(
    string Title,
    string FilePath,
    AssetType Type,
    int OrderIndex,
    string? Section = null);

/// <summary>
/// Metadata read back from an FFmpeg probe. All members are null when the probe found
/// nothing usable (or FFmpeg is unavailable), so callers persist whatever is present.
/// </summary>
public sealed record ProbeResult(TimeSpan? Duration, string? Codec, string? Resolution)
{
    public static readonly ProbeResult Empty = new(null, null, null);

    public bool HasAny => Duration is not null || Codec is not null || Resolution is not null;
}

/// <summary>
/// A course the user confirmed in the wizard: the (possibly renamed) title plus the assets
/// that survived unticking. Handed back from the wizard to the coordinator for persistence.
/// </summary>
public sealed record ConfirmedCourse(
    string Title,
    string FolderPath,
    IReadOnlyList<DetectedAsset> Assets);

/// <summary>What the wizard returns on confirm; null return means the user cancelled.</summary>
public sealed record ImportWizardResult(IReadOnlyList<ConfirmedCourse> Courses);
