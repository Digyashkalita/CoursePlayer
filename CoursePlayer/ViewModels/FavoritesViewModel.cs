namespace CoursePlayer.ViewModels;

/// <summary>Courses flagged as favourites. Populated in Phase 3.</summary>
public partial class FavoritesViewModel : PageViewModelBase
{
    public FavoritesViewModel()
    {
        Title = "Favorites";
    }

    public string Placeholder =>
        "Courses you mark with the heart icon will collect here.";
}
