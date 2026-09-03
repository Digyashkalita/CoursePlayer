using System.Windows;
using System.Windows.Threading;
using CoursePlayer.Data;
using CoursePlayer.Services;
using CoursePlayer.ViewModels;
using CoursePlayer.Views;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace CoursePlayer;

public partial class App : Application
{
    private ServiceProvider? _services;
    private Microsoft.Extensions.Logging.ILogger? _logger;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppPaths();

        try
        {
            paths.EnsureCreated();
        }
        catch (Exception ex)
        {
            // Without a writable app folder there is nowhere to log to, so this is the one
            // failure we report raw and bail on.
            MessageBox.Show(
                $"CoursePlayer could not create its data folder at:\n{paths.RootDirectory}\n\n{ex.Message}",
                "CoursePlayer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        ConfigureSerilog(paths);
        AttachGlobalExceptionHandlers();

        _services = BuildServiceProvider(paths);
        _logger = _services.GetRequiredService<ILoggerFactory>().CreateLogger("CoursePlayer.App");

        _logger.LogInformation("CoursePlayer starting. Data folder: {Root}", paths.RootDirectory);

        // FFmpeg must be pointed at before any FFME element is constructed.
        var ffmpeg = _services.GetRequiredService<IFFmpegBootstrapper>();
        ffmpeg.Initialize();

        try
        {
            await _services.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Database initialisation failed.");
            MessageBox.Show(
                $"CoursePlayer could not open its database at:\n{paths.DatabasePath}\n\n" +
                $"{ex.Message}\n\nA log has been written to:\n{paths.LogDirectory}",
                "CoursePlayer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        RegisterViews(_services.GetRequiredService<INavigationService>());

        // Apply the saved matte theme before the window is shown so it never flashes the
        // default palette first.
        var theme = _services.GetRequiredService<IThemeService>();
        theme.Initialize();

        var window = _services.GetRequiredService<MainWindow>();
        window.DataContext = _services.GetRequiredService<MainWindowViewModel>();

        // The caption bar is a per-window property, so it has to be painted on the instance —
        // the theme was applied before this window existed.
        theme.ApplyWindowChrome(window);

        MainWindow = window;
        window.Show();

        // Loading FFmpeg takes a moment, so do it once the window is already up.
        _ = ffmpeg.PreloadAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("CoursePlayer exiting with code {ExitCode}.", e.ApplicationExitCode);

        _services?.Dispose();
        Log.CloseAndFlush();

        base.OnExit(e);
    }

    private static ServiceProvider BuildServiceProvider(AppPaths paths)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: false);
        });

        services.AddSingleton<IAppPaths>(paths);

        // A factory rather than a scoped context: WPF has no ambient scope, and every
        // unit of work in DatabaseWriter wants its own short-lived context.
        services.AddDbContextFactory<CoursePlayerDbContext>(options =>
            options.UseSqlite($"Data Source={paths.DatabasePath}"));

        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        services.AddSingleton<IDatabaseWriter, DatabaseWriter>();
        services.AddSingleton<IFFmpegBootstrapper, FFmpegBootstrapper>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IFilePickerService, FilePickerService>();
        services.AddSingleton<IFolderScanner, FolderScanner>();
        services.AddSingleton<IMediaProbe, MediaProbe>();
        services.AddSingleton<IImportWizardService, ImportWizardService>();
        services.AddSingleton<IImportCoordinator, ImportCoordinator>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IAssetPlaybackService, AssetPlaybackService>();
        services.AddSingleton<IThumbnailService, ThumbnailService>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        // Pages are transient so revisiting a section starts from a clean state.
        services.AddTransient<HomeViewModel>();
        services.AddTransient<FavoritesViewModel>();
        services.AddTransient<RecentViewModel>();
        services.AddTransient<SearchViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<CourseDetailViewModel>();
        services.AddTransient<VideoPlayerViewModel>();

        services.AddTransient<HomeView>();
        services.AddTransient<FavoritesView>();
        services.AddTransient<RecentView>();
        services.AddTransient<SearchView>();
        services.AddTransient<SettingsView>();
        services.AddTransient<CourseDetailView>();
        services.AddTransient<VideoPlayerView>();

        return services.BuildServiceProvider();
    }

    private static void RegisterViews(INavigationService navigation)
    {
        navigation.Register<HomeViewModel, HomeView>();
        navigation.Register<FavoritesViewModel, FavoritesView>();
        navigation.Register<RecentViewModel, RecentView>();
        navigation.Register<SearchViewModel, SearchView>();
        navigation.Register<SettingsViewModel, SettingsView>();
        navigation.Register<CourseDetailViewModel, CourseDetailView>();
        navigation.Register<VideoPlayerViewModel, VideoPlayerView>();
    }

    private static void ConfigureSerilog(IAppPaths paths)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogDirectory, "courseplayer-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 16L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private void AttachGlobalExceptionHandlers()
    {
        // One unhandled exception should log and warn, not silently kill the app.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(
                args.ExceptionObject as Exception,
                "Unhandled AppDomain exception. Terminating: {Terminating}",
                args.IsTerminating);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled dispatcher exception.");

        var paths = _services?.GetService<IAppPaths>();
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\n" +
            "The app will try to keep running. Details were written to the log" +
            (paths is null ? "." : $":\n{paths.LogDirectory}"),
            "CoursePlayer",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }
}
