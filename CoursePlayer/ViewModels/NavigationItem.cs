using CommunityToolkit.Mvvm.ComponentModel;
using MaterialDesignThemes.Wpf;

namespace CoursePlayer.ViewModels;

/// <summary>
/// One entry in the shell sidebar.
/// </summary>
public partial class NavigationItem : ObservableObject
{
    public NavigationItem(string label, PackIconKind icon, Type viewModelType)
    {
        Label = label;
        Icon = icon;
        ViewModelType = viewModelType;
    }

    public string Label { get; }

    public PackIconKind Icon { get; }

    public Type ViewModelType { get; }

    [ObservableProperty]
    private bool _isSelected;
}
