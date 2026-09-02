using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Microsoft.Extensions.Logging;

namespace CoursePlayer.Services;

/// <summary>
/// A named, matte color scheme. Holds every <c>App.Color.*</c> value the app paints with,
/// so applying one retints the whole UI in a single pass.
/// </summary>
public sealed record ThemePalette(
    string Id,
    string Name,
    Color Background,
    Color Surface,
    Color SurfaceRaised,
    Color Hover,
    Color Divider,
    Color TextPrimary,
    Color TextSecondary,
    Color TextDisabled,
    Color Accent,
    Color AccentDim,
    Color Warning);

/// <summary>
/// Swaps the app's color palette at runtime. Every view references brushes as
/// <c>{StaticResource App.Brush.*}</c> bound to shared brush instances, so mutating those
/// instances' <see cref="SolidColorBrush.Color"/> retints the live UI without touching XAML.
/// </summary>
public interface IThemeService
{
    IReadOnlyList<ThemePalette> Palettes { get; }

    ThemePalette Current { get; }

    /// <summary>Applies the palette saved in settings (or the default) — called once at startup.</summary>
    void Initialize();

    /// <summary>Applies a palette live and persists the choice.</summary>
    void SelectPalette(ThemePalette palette);
}

/// <inheritdoc cref="IThemeService"/>
public sealed class ThemeService : IThemeService
{
    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

    /// <summary>Perceived luminance (0..1) of a color, used to pick a readable foreground.</summary>
    private static double Luminance(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var f = (double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
    }

    /// <summary>White for dark accents, black for light accents — so buttons stay legible on the title bar.</summary>
    private static Color ContrastingForeground(Color accent) =>
        Luminance(accent) > 0.5 ? Colors.Black : Colors.White;

    // Matte = muted, desaturated. Dark base kept; the harsh neon-red accent is gone.
    public static readonly ThemePalette Graphite = new(
        Id: "graphite",
        Name: "Graphite",
        Background: C("#FF1E1E20"),
        Surface: C("#FF26262A"),
        SurfaceRaised: C("#FF2F2F34"),
        Hover: C("#FF35353B"),
        Divider: C("#FF3A3A40"),
        TextPrimary: C("#FFECECEE"),
        TextSecondary: C("#FFA6A6AD"),
        TextDisabled: C("#FF6E6E75"),
        Accent: C("#FF5E8B7E"),
        AccentDim: C("#FF4C7267"),
        Warning: C("#FFD8A657"));

    public static readonly ThemePalette SlateBlue = new(
        Id: "slate-blue",
        Name: "Slate Blue",
        Background: C("#FF1C2029"),
        Surface: C("#FF262B36"),
        SurfaceRaised: C("#FF303644"),
        Hover: C("#FF39404F"),
        Divider: C("#FF3E4552"),
        TextPrimary: C("#FFE9ECF2"),
        TextSecondary: C("#FFA3ABBC"),
        TextDisabled: C("#FF6B7284"),
        Accent: C("#FF6C87A8"),
        AccentDim: C("#FF566E8B"),
        Warning: C("#FFCBA15A"));

    // The original YouTube-dark scheme, kept so nothing is lost.
    public static readonly ThemePalette Crimson = new(
        Id: "crimson",
        Name: "Crimson (classic)",
        Background: C("#FF0F0F0F"),
        Surface: C("#FF181818"),
        SurfaceRaised: C("#FF212121"),
        Hover: C("#FF272727"),
        Divider: C("#FF303030"),
        TextPrimary: C("#FFF1F1F1"),
        TextSecondary: C("#FFAAAAAA"),
        TextDisabled: C("#FF717171"),
        Accent: C("#FFFF0033"),
        AccentDim: C("#FFCC0029"),
        Warning: C("#FFFFC107"));

    private readonly ISettingsService _settings;
    private readonly ILogger<ThemeService> _logger;

    public ThemeService(ISettingsService settings, ILogger<ThemeService> logger)
    {
        _settings = settings;
        _logger = logger;
        Current = Graphite;
    }

    public IReadOnlyList<ThemePalette> Palettes { get; } = [Graphite, SlateBlue, Crimson];

    public ThemePalette Current { get; private set; }

    public void Initialize()
    {
        var savedId = _settings.Current.ThemeId;
        var palette = Palettes.FirstOrDefault(p => p.Id == savedId) ?? Graphite;
        ApplyPalette(palette);
    }

    public void SelectPalette(ThemePalette palette)
    {
        ApplyPalette(palette);
        _settings.Save(_settings.Current with { ThemeId = palette.Id });
    }

    private void ApplyPalette(ThemePalette palette)
    {
        var app = Application.Current;
        if (app is null)
        {
            Current = palette;
            return;
        }

        void Apply()
        {
            try
            {
                var map = new Dictionary<string, Color>
                {
                    ["App.Brush.Background"] = palette.Background,
                    ["App.Brush.Surface"] = palette.Surface,
                    ["App.Brush.SurfaceRaised"] = palette.SurfaceRaised,
                    ["App.Brush.Hover"] = palette.Hover,
                    ["App.Brush.Divider"] = palette.Divider,
                    ["App.Brush.TextPrimary"] = palette.TextPrimary,
                    ["App.Brush.TextSecondary"] = palette.TextSecondary,
                    ["App.Brush.TextDisabled"] = palette.TextDisabled,
                    ["App.Brush.Accent"] = palette.Accent,
                    ["App.Brush.AccentDim"] = palette.AccentDim,
                    ["App.Brush.Warning"] = palette.Warning,
                };

                // Always replace the entry with a fresh, mutable brush. The XAML references
                // these keys via {DynamicResource}, so WPF re-resolves on every change and the
                // whole UI retints. Mutating a frozen brush in place would silently do nothing
                // for the many elements that captured the StaticResource at load time.
                foreach (var (key, color) in map)
                {
                    app.Resources[key] = new SolidColorBrush(color);
                }

                // Also update MahApps accent brushes so window chrome (title bar, etc.) follows the palette.
                // MahApps 2.4 exposes its accent under "MahApps.Brushes.*" (the old AccentColorBrush keys are gone).
                try
                {
                    var contrasting = ContrastingForeground(palette.Accent);
                    var contrastingBrush = new SolidColorBrush(contrasting);

                    // 1) Update application-scoped MahApps brushes
                    //    Accent* and Highlight -> palette.Accent
                    //    IdealForeground -> contrasting color (for glyphs and text that needs to pop)
                    var accentBrush = new SolidColorBrush(palette.Accent);
                    foreach (var key in new[] { "MahApps.Brushes.Accent", "MahApps.Brushes.Accent2", "MahApps.Brushes.Accent3", "MahApps.Brushes.Accent4", "MahApps.Brushes.AccentBase", "MahApps.Brushes.Highlight" })
                    {
                        if (app.Resources.Contains(key))
                            app.Resources[key] = accentBrush;
                    }
                    if (app.Resources.Contains("MahApps.Brushes.IdealForeground"))
                        app.Resources["MahApps.Brushes.IdealForeground"] = contrastingBrush;

                    // 2) Update the same keys inside the merged MahApps theme dictionary (Dark.Red.xaml).
                    foreach (ResourceDictionary dict in app.Resources.MergedDictionaries)
                    {
                        foreach (var key in new[] { "MahApps.Brushes.Accent", "MahApps.Brushes.AccentBase", "MahApps.Brushes.Highlight" })
                        {
                            if (dict.Contains(key))
                                dict[key] = accentBrush;
                        }
                        if (dict.Contains("MahApps.Brushes.IdealForeground"))
                            dict["MahApps.Brushes.IdealForeground"] = contrastingBrush;
                    }

                    // 3) Explicitly retint any open MetroWindow title bars.
                    if (app.MainWindow is MahApps.Metro.Controls.MetroWindow metro)
                    {
                        // Title bar background = accent
                        metro.WindowTitleBrush = accentBrush;
                        metro.NonActiveWindowTitleBrush = accentBrush;
                        // Title text and window button glyphs = contrasting color (so they are visible on the accent bar)
                        metro.TitleForeground = contrastingBrush;
                        // Ensure the window button glyphs use IdealForeground (which we set to contrasting above)
                        // No need to set OverrideDefaultWindowCommandsBrush; we want the button background to remain
                        // transparent (showing the title bar accent) and the glyphs contrasting.
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update MahApps accent resources.");
                }

                ApplyMaterialDesignAccent(palette.Accent);
                Current = palette;
                _logger.LogInformation("Applied theme {Theme}.", palette.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply theme {Theme}.", palette.Name);
            }
        }

        if (app.Dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            app.Dispatcher.Invoke(Apply);
        }
    }

    // Nudge the MaterialDesign primary/secondary so built-in controls (checkboxes, ripples,
    // MahApps chrome via the bridge) match the accent. Best-effort; never fatal.
    private void ApplyMaterialDesignAccent(Color accent)
    {
        try
        {
            var helper = new PaletteHelper();
            var theme = helper.GetTheme();
            theme.SetPrimaryColor(accent);
            theme.SetSecondaryColor(accent);
            helper.SetTheme(theme);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update MaterialDesign accent; app brushes still applied.");
        }
    }
}
