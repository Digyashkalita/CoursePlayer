using System.Windows;
using CoursePlayer.ViewModels;
using CoursePlayer.Views;

namespace CoursePlayer.Services;

/// <summary>
/// Shows the import wizard modally and hands back the user's confirmed selection. The only
/// import-pipeline type that touches WPF, so <see cref="ImportCoordinator"/> stays UI-free.
/// </summary>
public interface IImportWizardService
{
    /// <summary>
    /// Displays the wizard for <paramref name="scan"/> and blocks until the user confirms or
    /// cancels. Returns the confirmed courses, or null if cancelled. Safe to call from any
    /// thread — it marshals to the UI thread internally.
    /// </summary>
    Task<ImportWizardResult?> ShowAsync(ScanResult scan);
}

/// <inheritdoc cref="IImportWizardService"/>
public sealed class ImportWizardService : IImportWizardService
{
    private readonly IThemeService _theme;

    public ImportWizardService(IThemeService theme)
    {
        _theme = theme;
    }

    public Task<ImportWizardResult?> ShowAsync(ScanResult scan)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            // No UI (e.g. a test host) — nothing to show.
            return Task.FromResult<ImportWizardResult?>(null);
        }

        return dispatcher.InvokeAsync(() => ShowModal(scan)).Task;
    }

    private ImportWizardResult? ShowModal(ScanResult scan)
    {
        var viewModel = new ImportWizardViewModel(scan);
        var window = new ImportWizardWindow
        {
            DataContext = viewModel,
            Owner = Application.Current?.MainWindow,
        };

        // Caption colours are per-window properties, so a new window starts on the stock
        // MahApps chrome until the palette is pushed onto it.
        _theme.ApplyWindowChrome(window);

        window.ShowDialog();
        return viewModel.Result;
    }
}
