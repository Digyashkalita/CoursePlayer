using CoursePlayer.Data;

namespace CoursePlayer.Services;

/// <summary>
/// Maps file extensions to the <see cref="AssetType"/> viewer that will render them.
/// The single source of truth for "is this file something CoursePlayer can show?".
/// </summary>
public static class FileTypeRegistry
{
    // Case-insensitive: extensions come straight off disk in whatever casing the file has.
    private static readonly IReadOnlyDictionary<string, AssetType> ExtensionMap =
        new Dictionary<string, AssetType>(StringComparer.OrdinalIgnoreCase)
        {
            [".mp4"] = AssetType.Video,
            [".mkv"] = AssetType.Video,
            [".mov"] = AssetType.Video,
            [".avi"] = AssetType.Video,
            [".m4v"] = AssetType.Video,
            [".webm"] = AssetType.Video,
            [".wmv"] = AssetType.Video,
            [".flv"] = AssetType.Video,
            [".mpg"] = AssetType.Video,
            [".mpeg"] = AssetType.Video,
            [".ts"] = AssetType.Video,

            [".pdf"] = AssetType.Pdf,

            [".docx"] = AssetType.Docx,
            [".doc"] = AssetType.Docx,

            [".txt"] = AssetType.Text,
            [".rtf"] = AssetType.Text,
            [".md"] = AssetType.Text,
        };

    /// <summary>
    /// Returns the asset type for <paramref name="path"/>, or null when the extension is not
    /// something we import. Null means "skip this file", distinct from
    /// <see cref="AssetType.Unknown"/> which is reserved for a recognised file that failed to probe.
    /// </summary>
    public static AssetType? Classify(string path) =>
        ExtensionMap.TryGetValue(Path.GetExtension(path), out var type) ? type : null;

    /// <summary>True when the file has a recognised, importable extension.</summary>
    public static bool IsSupported(string path) => Classify(path) is not null;

    /// <summary>True when the file is a video, so the coordinator knows to probe it.</summary>
    public static bool IsVideo(string path) => Classify(path) == AssetType.Video;
}
