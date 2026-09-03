using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using Unosquare.FFME;
using Unosquare.FFME.Common;

namespace CoursePlayer.Services;

/// <inheritdoc cref="IAssetPlaybackService"/>
public sealed class AssetPlaybackService : IAssetPlaybackService, IDisposable
{
    private readonly ILogger<AssetPlaybackService> _logger;
    private readonly Unosquare.FFME.MediaElement _mediaElement;

    private double _volume = 1d;
    private bool _isMuted;
    private double _speedRatio = 1d;
    private bool _isDisposed;

    public AssetPlaybackService(ILogger<AssetPlaybackService> logger)
    {
        _logger = logger;

        // Constructing the element requires the UI thread; App resolves this service before
        // the window is shown, so we are already on it, but be defensive for safety.
        _mediaElement = OnUi(() =>
        {
            var element = new Unosquare.FFME.MediaElement
            {
                // The view model drives every transition explicitly.
                LoadedBehavior = MediaPlaybackState.Manual,
                UnloadedBehavior = MediaPlaybackState.Manual,
                Stretch = System.Windows.Media.Stretch.Uniform,
                Background = System.Windows.Media.Brushes.Black,
                // Lets the scrubber show a frame while the clock is paused.
                ScrubbingEnabled = true,
                // FFME's vsync waiter P/Invokes D3DKMTWaitForVerticalBlankEvent, and that
                // indirect call trips Control Flow Guard (fail-fast 0xc0000409 subcode 0xa)
                // when the sync context is rebuilt - i.e. on every lesson change. WPF already
                // composites on its own render clock, so switching this off costs nothing.
                VerticalSyncEnabled = false,
                Volume = _volume,
                IsMuted = _isMuted,
            };

            element.MediaOpening += OnMediaOpening;
            element.MediaOpened += OnMediaOpened;
            element.MediaEnded += OnMediaEnded;
            element.MediaFailed += OnMediaFailed;
            element.RenderingSubtitles += OnRenderingSubtitles;
            element.MessageLogged += OnMessageLogged;

            // FFME's DirectSound renderer calls the device through hand-built COM vtable
            // pointers. Tearing one down while the render worker is still using it calls a
            // freed pointer, which Control Flow Guard turns into an immediate process
            // fail-fast (0xc0000409, subcode 0xa) - exactly what killed us on lesson changes.
            // The legacy waveOut path uses ordinary P/Invoke and survives the same churn.
            element.RendererOptions.UseLegacyAudioOut = true;

            return element;
        });
    }

    public Unosquare.FFME.MediaElement? MediaElement => _isDisposed ? null : _mediaElement;

    public TimeSpan Position => _isDisposed ? TimeSpan.Zero : _mediaElement.Position;

    public TimeSpan? NaturalDuration => _isDisposed ? null : _mediaElement.NaturalDuration;

    public double BufferingProgress => _isDisposed ? 0d : _mediaElement.BufferingProgress;

    public bool IsOpen => !_isDisposed && _mediaElement.IsOpen;

    public bool IsPlaying => !_isDisposed && _mediaElement.IsPlaying;

    public bool IsSeekable => !_isDisposed && _mediaElement.IsSeekable;

    public (int Width, int Height)? VideoSize
    {
        get
        {
            if (_isDisposed || !_mediaElement.HasVideo)
            {
                return null;
            }

            var width = _mediaElement.NaturalVideoWidth;
            var height = _mediaElement.NaturalVideoHeight;
            return width > 0 && height > 0 ? (width, height) : null;
        }
    }

    public bool HasSubtitles => !_isDisposed && _mediaElement.HasSubtitles;

    public string? SubtitlesSource { get; set; }

    public bool AreSubtitlesEnabled { get; set; } = true;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0d, 1d);
            if (!_isDisposed)
            {
                OnUi(() => _mediaElement.Volume = _volume);
            }
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            if (!_isDisposed)
            {
                OnUi(() => _mediaElement.IsMuted = _isMuted);
            }
        }
    }

    public double SpeedRatio
    {
        get => _speedRatio;
        set
        {
            _speedRatio = Math.Clamp(value, 0.25d, 4d);
            if (!_isDisposed)
            {
                OnUi(() => _mediaElement.SpeedRatio = _speedRatio);
            }
        }
    }

    public async Task<bool> OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            _logger.LogWarning("Refusing to open missing media file {FilePath}.", filePath);
            return false;
        }

        // Open() closes any current media itself. Queuing an explicit Close() first puts two
        // commands in flight against the same container, which crashed the process (fail-fast
        // 0xc0000409) when a switch was started from inside FFME's own MediaEnded callback.
        var opened = await _mediaElement.Open(new Uri(filePath));
        if (!opened)
        {
            _logger.LogWarning("FFME declined to open {FilePath}.", filePath);
        }

        return opened;
    }

    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _mediaElement.Play();
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_mediaElement.CanPause)
        {
            await _mediaElement.Pause();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        await _mediaElement.Stop();
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return;
        }

        await _mediaElement.Close();
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!_mediaElement.IsSeekable)
        {
            return;
        }

        var target = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        if (_mediaElement.NaturalDuration is { } duration && target > duration)
        {
            target = duration;
        }

        await _mediaElement.Seek(target);
    }

    public event EventHandler? MediaOpened;

    public event EventHandler? MediaEnded;

    public event EventHandler<Exception>? MediaFailed;

    private void OnMediaOpening(object? sender, MediaOpeningEventArgs e)
    {
        // An external .srt can only be attached while the container is opening.
        if (!string.IsNullOrWhiteSpace(SubtitlesSource) && File.Exists(SubtitlesSource))
        {
            e.Options.SubtitlesSource = SubtitlesSource;
        }
    }

    private void OnMediaOpened(object? sender, MediaOpenedEventArgs e) =>
        MediaOpened?.Invoke(this, EventArgs.Empty);

    private void OnMediaEnded(object? sender, EventArgs e) =>
        MediaEnded?.Invoke(this, EventArgs.Empty);

    private void OnMediaFailed(object? sender, MediaFailedEventArgs e)
    {
        _logger.LogError(e.ErrorException, "Media playback failed.");
        MediaFailed?.Invoke(this, e.ErrorException);
    }

    private void OnRenderingSubtitles(object? sender, RenderingSubtitlesEventArgs e)
    {
        // Cancelling the render is the documented way to hide subtitles live.
        if (!AreSubtitlesEnabled)
        {
            e.Cancel = true;
        }
    }

    /// <summary>
    /// Mirrors FFME's own engine log into ours. Its warnings are the only visible trace of a
    /// decoder or renderer problem that ends in a process-level fail-fast.
    /// </summary>
    private void OnMessageLogged(object? sender, MediaLogMessageEventArgs e)
    {
        switch (e.MessageType)
        {
            case MediaLogMessageType.Error:
                _logger.LogError("FFME {Aspect}: {Message}", e.AspectName, e.Message);
                break;
            case MediaLogMessageType.Warning:
                _logger.LogWarning("FFME {Aspect}: {Message}", e.AspectName, e.Message);
                break;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(AssetPlaybackService));
        }
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    private static T OnUi<T>(Func<T> func)
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.CheckAccess() ? func() : dispatcher.Invoke(func);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        _mediaElement.MediaOpening -= OnMediaOpening;
        _mediaElement.MediaOpened -= OnMediaOpened;
        _mediaElement.MediaEnded -= OnMediaEnded;
        _mediaElement.MediaFailed -= OnMediaFailed;
        _mediaElement.RenderingSubtitles -= OnRenderingSubtitles;
        _mediaElement.MessageLogged -= OnMessageLogged;

        OnUi(() => _mediaElement.Dispose());
    }
}