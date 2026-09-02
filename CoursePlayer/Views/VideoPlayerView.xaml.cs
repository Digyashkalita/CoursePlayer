using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CoursePlayer.ViewModels;

namespace CoursePlayer.Views;

/// <summary>
/// Player chrome. Three jobs the XAML cannot do on its own: parent the shared FFME element
/// into this view's tree, translate raw mouse/key input into view-model calls, and drive
/// fullscreen on the hosting window.
/// </summary>
public partial class VideoPlayerView : UserControl
{
    private Unosquare.FFME.MediaElement? _hostedMedia;
    private Window? _window;
    private WindowState _previousWindowState;
    private WindowStyle _previousWindowStyle;
    private bool _previousTopmost;
    private bool _isWindowFullscreen;

    public VideoPlayerView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private VideoPlayerViewModel? ViewModel => DataContext as VideoPlayerViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachMedia();

        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            // Tunnelling handler: shortcuts win even when a slider or combo box has focus.
            _window.PreviewKeyDown += OnWindowPreviewKeyDown;
            _window.PreviewMouseMove += OnWindowPreviewMouseMove;
        }

        // Keyboard shortcuts declared in XAML need focus somewhere inside this view.
        Focus();
        Keyboard.Focus(this);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            _window.PreviewKeyDown -= OnWindowPreviewKeyDown;
            _window.PreviewMouseMove -= OnWindowPreviewMouseMove;
        }

        if (_isWindowFullscreen)
        {
            ExitWindowFullscreen();
        }

        DetachMedia();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is VideoPlayerViewModel oldViewModel)
        {
            oldViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is VideoPlayerViewModel newViewModel)
        {
            newViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        AttachMedia();
    }

    /// <summary>
    /// The MediaElement lives in the playback service, not the XAML: only the element that
    /// actually decodes can render, so it has to be parented into the live visual tree.
    /// </summary>
    private void AttachMedia()
    {
        var media = ViewModel?.MediaElement;
        if (media is null || ReferenceEquals(_hostedMedia, media))
        {
            return;
        }

        DetachMedia();

        // A shared element may still be parented by a previous instance of this view.
        if (media.Parent is Border previousHost && !ReferenceEquals(previousHost, VideoHost))
        {
            previousHost.Child = null;
        }
        else if (media.Parent is ContentControl previousContent)
        {
            previousContent.Content = null;
        }

        VideoHost.Child = media;
        _hostedMedia = media;
    }

    private void DetachMedia()
    {
        if (_hostedMedia is not null && ReferenceEquals(VideoHost.Child, _hostedMedia))
        {
            VideoHost.Child = null;
        }

        _hostedMedia = null;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoPlayerViewModel.MediaElement))
        {
            AttachMedia();
        }
        else if (e.PropertyName == nameof(VideoPlayerViewModel.IsFullscreen))
        {
            ApplyFullscreen(ViewModel?.IsFullscreen == true);
        }
    }

    // ----------------------------- input plumbing -----------------------------

    private void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        // Any movement anywhere in the window counts as activity, matching YouTube.
        ViewModel?.NotifyActivity();
    }

    private void OnStageClicked(object sender, MouseButtonEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        // Clicking the picture toggles playback; also pull focus back for shortcuts.
        Focus();
        Keyboard.Focus(this);

        if (viewModel.PlayPauseCommand.CanExecute(null))
        {
            viewModel.PlayPauseCommand.Execute(null);
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null || !IsVisible)
        {
            return;
        }

        // Let text entry through untouched.
        if (Keyboard.FocusedElement is TextBox or PasswordBox)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left:
                Execute(viewModel.RewindCommand);
                e.Handled = true;
                break;

            case Key.Right:
                Execute(viewModel.ForwardCommand);
                e.Handled = true;
                break;

            case Key.Up:
                viewModel.StepVolume(5d);
                e.Handled = true;
                break;

            case Key.Down:
                viewModel.StepVolume(-5d);
                e.Handled = true;
                break;

            case Key.Space:
                Execute(viewModel.PlayPauseCommand);
                e.Handled = true;
                break;

            case Key.Escape when viewModel.IsFullscreen:
                Execute(viewModel.ToggleFullscreenCommand);
                e.Handled = true;
                break;
        }
    }

    private static void Execute(ICommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    // ------------------------------- scrubbing --------------------------------

    private void OnScrubberDragStarted(object sender, DragStartedEventArgs e) => ViewModel?.BeginScrub();

    private async void OnScrubberDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            await viewModel.EndScrubAsync();
        }
    }

    /// <summary>
    /// IsMoveToPointEnabled jumps the value without ever raising a drag, so a plain click
    /// needs its own seek.
    /// </summary>
    private async void OnScrubberClicked(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is { } viewModel && !viewModel.IsScrubbing)
        {
            await viewModel.EndScrubAsync();
        }
    }

    // ------------------------------- fullscreen -------------------------------

    private void ApplyFullscreen(bool shouldBeFullscreen)
    {
        if (shouldBeFullscreen == _isWindowFullscreen)
        {
            return;
        }

        if (shouldBeFullscreen)
        {
            EnterWindowFullscreen();
        }
        else
        {
            ExitWindowFullscreen();
        }
    }

    private void EnterWindowFullscreen()
    {
        _window ??= Window.GetWindow(this);
        if (_window is null)
        {
            return;
        }

        _previousWindowState = _window.WindowState;
        _previousWindowStyle = _window.WindowStyle;
        _previousTopmost = _window.Topmost;

        _window.WindowStyle = WindowStyle.None;
        _window.Topmost = true;
        _window.WindowState = WindowState.Maximized;

        _isWindowFullscreen = true;
    }

    private void ExitWindowFullscreen()
    {
        if (_window is null)
        {
            _isWindowFullscreen = false;
            return;
        }

        _window.WindowStyle = _previousWindowStyle;
        _window.Topmost = _previousTopmost;
        _window.WindowState = _previousWindowState;

        _isWindowFullscreen = false;
    }
}