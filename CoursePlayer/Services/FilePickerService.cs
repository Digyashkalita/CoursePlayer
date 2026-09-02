using Microsoft.Win32;

namespace CoursePlayer.Services;

/// <summary>
/// Native folder / file pickers, behind an interface so view models stay testable.
/// </summary>
public interface IFilePickerService
{
    /// <summary>Returns the chosen folder, or null if the user cancelled.</summary>
    string? PickFolder(string title);

    /// <summary>Returns the chosen image file, or null if the user cancelled.</summary>
    string? PickImage(string title);
}

/// <inheritdoc cref="IFilePickerService"/>
public sealed class FilePickerService : IFilePickerService
{
    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickImage(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Multiselect = false,
            Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All files|*.*",
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
