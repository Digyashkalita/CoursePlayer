using CommunityToolkit.Mvvm.ComponentModel;
using CoursePlayer.Data;
using CoursePlayer.Services;

namespace CoursePlayer.ViewModels;

/// <summary>
/// One reviewable row in the import wizard: a proposed course the user can rename, untick,
/// or leave as-is before importing.
/// </summary>
public partial class DetectedCourseViewModel : ObservableObject
{
    public DetectedCourseViewModel(DetectedCourse detected)
    {
        FolderPath = detected.FolderPath;
        Assets = detected.Assets;
        _title = detected.SuggestedTitle;
        Summary = BuildSummary(detected.Assets);
    }

    /// <summary>Ticked courses are the ones that get imported. On by default.</summary>
    [ObservableProperty]
    private bool _isSelected = true;

    /// <summary>Editable course name; seeded from the folder name.</summary>
    [ObservableProperty]
    private string _title;

    public string FolderPath { get; }

    /// <summary>Human-readable content breakdown, e.g. "12 videos, 2 PDFs".</summary>
    public string Summary { get; }

    public IReadOnlyList<DetectedAsset> Assets { get; }

    private static string BuildSummary(IReadOnlyList<DetectedAsset> assets)
    {
        var videos = 0;
        var pdfs = 0;
        var docs = 0;
        var texts = 0;

        foreach (var asset in assets)
        {
            switch (asset.Type)
            {
                case AssetType.Video: videos++; break;
                case AssetType.Pdf: pdfs++; break;
                case AssetType.Docx: docs++; break;
                case AssetType.Text: texts++; break;
            }
        }

        var parts = new List<string>(4);
        if (videos > 0) parts.Add(Plural(videos, "video", "videos"));
        if (pdfs > 0) parts.Add(Plural(pdfs, "PDF", "PDFs"));
        if (docs > 0) parts.Add(Plural(docs, "document", "documents"));
        if (texts > 0) parts.Add(Plural(texts, "text file", "text files"));

        if (parts.Count == 0)
        {
            return "No files";
        }

        var summary = string.Join(", ", parts);

        // Surface the preserved folder hierarchy so the user sees subfolders became sections,
        // not separate courses.
        var sections = assets
            .Select(a => a.Section)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return sections > 1 ? $"{summary} in {sections} sections" : summary;
    }

    private static string Plural(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";
}
