namespace CoursePlayer.ViewModels;

/// <summary>Recently opened courses. Populated in Phase 3.</summary>
public partial class RecentViewModel : PageViewModelBase
{
    public RecentViewModel()
    {
        Title = "Recent";
    }

    public string Placeholder =>
        "Courses you have opened will appear here, most recent first.";
}
