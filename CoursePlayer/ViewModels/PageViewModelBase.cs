using CommunityToolkit.Mvvm.ComponentModel;

namespace CoursePlayer.ViewModels;

/// <summary>
/// Shared base for anything hosted in the shell's content frame.
/// </summary>
public abstract partial class PageViewModelBase : ObservableObject
{
    /// <summary>Shown in the shell header.</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>True while the page is loading data; drives the busy indicator.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Non-null when the page failed to load; rendered inline instead of a dialog.</summary>
    [ObservableProperty]
    private string? _errorMessage;
}
