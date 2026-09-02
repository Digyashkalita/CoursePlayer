namespace CoursePlayer.Data;

/// <summary>
/// The kind of content an <see cref="Asset"/> holds. This drives which viewer the
/// shell navigates to when the asset is opened.
/// </summary>
public enum AssetType
{
    /// <summary>Recognised extension, but the file could not be read or probed.</summary>
    Unknown = 0,
    Video = 1,
    Pdf = 2,
    /// <summary>Word processing document (.docx, .doc) rendered by the Open XML viewer.</summary>
    Docx = 3,
    /// <summary>Plain text or rich text (.txt, .rtf) rendered by the reading view.</summary>
    Text = 4,
}
