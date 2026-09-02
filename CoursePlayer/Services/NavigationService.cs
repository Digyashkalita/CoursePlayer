using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CoursePlayer.Services;

/// <summary>
/// A view model that needs an argument (a course id, an asset id) when navigated to.
/// </summary>
public interface INavigationAware
{
    Task OnNavigatedToAsync(object? parameter, CancellationToken cancellationToken = default);
}

/// <summary>
/// A view model that should release resources when the shell navigates away — the video
/// player uses this to stop playback and flush the resume position.
/// </summary>
public interface INavigatedFromAware
{
    Task OnNavigatedFromAsync();
}

/// <summary>
/// Swaps views inside the shell's content <see cref="Frame"/>. View models and views are
/// both resolved from the container, so a view model can take services in its constructor.
/// </summary>
public interface INavigationService
{
    /// <summary>Attaches the shell's frame. Called once by the main window.</summary>
    void Initialize(Frame frame);

    /// <summary>Maps a view model type to the view that renders it.</summary>
    void Register<TViewModel, TView>()
        where TViewModel : class
        where TView : ContentControl;

    Task NavigateToAsync<TViewModel>(object? parameter = null) where TViewModel : class;

    Task NavigateToAsync(Type viewModelType, object? parameter = null);

    bool CanGoBack { get; }

    Task GoBackAsync();

    Type? CurrentViewModelType { get; }

    /// <summary>Raised after navigation completes, with the new view model type.</summary>
    event EventHandler<Type>? Navigated;
}

/// <inheritdoc cref="INavigationService"/>
public sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<NavigationService> _logger;
    private readonly Dictionary<Type, Type> _viewModelToView = [];
    private readonly Stack<(Type ViewModelType, object? Parameter)> _backStack = new();

    private Frame? _frame;
    private object? _currentViewModel;

    public NavigationService(IServiceProvider services, ILogger<NavigationService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public bool CanGoBack => _backStack.Count > 0;

    public Type? CurrentViewModelType { get; private set; }

    public event EventHandler<Type>? Navigated;

    public void Initialize(Frame frame)
    {
        _frame = frame;
        // The shell drives navigation, so the frame's own journal and chrome stay out of it.
        _frame.NavigationUIVisibility = System.Windows.Navigation.NavigationUIVisibility.Hidden;
    }

    public void Register<TViewModel, TView>()
        where TViewModel : class
        where TView : ContentControl
    {
        _viewModelToView[typeof(TViewModel)] = typeof(TView);
    }

    public Task NavigateToAsync<TViewModel>(object? parameter = null) where TViewModel : class =>
        NavigateToAsync(typeof(TViewModel), parameter);

    public async Task NavigateToAsync(Type viewModelType, object? parameter = null)
    {
        if (CurrentViewModelType is { } previous && !ReferenceEquals(previous, viewModelType))
        {
            _backStack.Push((previous, _currentParameter));
        }

        await NavigateCoreAsync(viewModelType, parameter).ConfigureAwait(true);
    }

    public async Task GoBackAsync()
    {
        if (!CanGoBack)
        {
            return;
        }

        var (viewModelType, parameter) = _backStack.Pop();
        await NavigateCoreAsync(viewModelType, parameter).ConfigureAwait(true);
    }

    private object? _currentParameter;

    private async Task NavigateCoreAsync(Type viewModelType, object? parameter)
    {
        if (_frame is null)
        {
            throw new InvalidOperationException(
                $"{nameof(NavigationService)} was used before {nameof(Initialize)} was called.");
        }

        if (!_viewModelToView.TryGetValue(viewModelType, out var viewType))
        {
            throw new InvalidOperationException(
                $"No view is registered for view model '{viewModelType.Name}'.");
        }

        if (_currentViewModel is INavigatedFromAware leaving)
        {
            try
            {
                await leaving.OnNavigatedFromAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Leaving a view must never block navigating to the next one.
                _logger.LogError(ex, "OnNavigatedFromAsync failed for {ViewModel}.", _currentViewModel.GetType().Name);
            }
        }

        var viewModel = _services.GetRequiredService(viewModelType);
        var view = (ContentControl)_services.GetRequiredService(viewType);
        view.DataContext = viewModel;

        _frame.Navigate(view);

        _currentViewModel = viewModel;
        _currentParameter = parameter;
        CurrentViewModelType = viewModelType;

        if (viewModel is INavigationAware arriving)
        {
            await arriving.OnNavigatedToAsync(parameter).ConfigureAwait(true);
        }

        _logger.LogDebug("Navigated to {ViewModel}.", viewModelType.Name);
        Navigated?.Invoke(this, viewModelType);
    }
}
