using CommunityToolkit.Mvvm.ComponentModel;
using CoursePlayer.Services;

namespace CoursePlayer.ViewModels;

/// <summary>
/// The Settings page. For now it hosts the theme picker; selecting a palette retints the
/// app immediately and persists the choice.
/// </summary>
public partial class SettingsViewModel : PageViewModelBase
{
    private readonly IThemeService _theme;

    public SettingsViewModel(IThemeService theme)
    {
        _theme = theme;
        Title = "Settings";
        Palettes = theme.Palettes;
        _selectedPalette = theme.Current;
    }

    public IReadOnlyList<ThemePalette> Palettes { get; }

    /// <summary>The chosen palette; setting it applies the theme live and saves it.</summary>
    [ObservableProperty]
    private ThemePalette _selectedPalette;

    partial void OnSelectedPaletteChanged(ThemePalette value)
    {
        if (value is not null && value.Id != _theme.Current.Id)
        {
            _theme.SelectPalette(value);
        }
    }
}
