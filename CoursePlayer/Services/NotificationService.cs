using System.Windows;
using MaterialDesignThemes.Wpf;

namespace CoursePlayer.Services;

/// <summary>
/// Non-blocking toasts. Used for warnings that must not interrupt playback or import —
/// network-path notices, playback fallbacks, missing files.
/// </summary>
public interface INotificationService
{
    /// <summary>Bound to the shell's Snackbar.</summary>
    SnackbarMessageQueue MessageQueue { get; }

    void Show(string message);

    void Show(string message, string actionLabel, Action action);
}

/// <inheritdoc cref="INotificationService"/>
public sealed class NotificationService : INotificationService
{
    public SnackbarMessageQueue MessageQueue { get; } = new(TimeSpan.FromSeconds(4));

    public void Show(string message) => OnUiThread(() => MessageQueue.Enqueue(message));

    public void Show(string message, string actionLabel, Action action) =>
        OnUiThread(() => MessageQueue.Enqueue(message, actionLabel, action));

    private static void OnUiThread(Action action)
    {
        // Callers include background scan/probe work, so marshal unconditionally.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.InvokeAsync(action);
        }
    }
}
