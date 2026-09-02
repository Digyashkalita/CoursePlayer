using CommunityToolkit.Mvvm.ComponentModel;

namespace CoursePlayer.ViewModels;

/// <summary>Search across courses and assets. Implemented in Phase 3.</summary>
public partial class SearchViewModel : PageViewModelBase
{
    public SearchViewModel()
    {
        Title = "Search";
    }

    [ObservableProperty]
    private string _query = string.Empty;

    public string Placeholder =>
        "Search across every course and lesson title.";
}
