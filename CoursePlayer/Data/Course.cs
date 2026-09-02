namespace CoursePlayer.Data;

/// <summary>
/// A course is an app-defined grouping of assets. It is deliberately NOT tied to a
/// folder: <see cref="FolderPath"/> only records where the files were first imported
/// from, and a course may later contain assets from several folders.
/// </summary>
public class Course
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Path to a generated or user-supplied 16:9 cover image. Null until one exists.</summary>
    public string? ThumbnailPath { get; set; }

    /// <summary>The folder this course was originally imported from. Watched for ghost files.</summary>
    public string FolderPath { get; set; } = string.Empty;

    public DateTimeOffset ImportedAt { get; set; }

    /// <summary>Set from the Favorites sidebar section.</summary>
    public bool IsFavorite { get; set; }

    /// <summary>Drives the Recent sidebar section. Null until the course is first opened.</summary>
    public DateTimeOffset? LastOpenedAt { get; set; }

    public ICollection<Asset> Assets { get; set; } = new List<Asset>();
}
