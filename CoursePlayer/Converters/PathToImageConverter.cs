using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace CoursePlayer.Converters;

/// <summary>
/// Turns an absolute image path into a decoded, frozen bitmap. Decoding happens up front
/// (<see cref="BitmapCacheOption.OnLoad"/>) so the file is released immediately — otherwise
/// WPF would keep a handle open and "Clear Cache" could not delete the thumbnail. The image
/// cache is bypassed as well, so a regenerated cover replaces the old one on screen.
/// A missing or unreadable file converts to null, which leaves the fallback icon visible.
/// </summary>
public sealed class PathToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();

            // Frozen bitmaps are shareable and cheaper for the render thread.
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            // A half-written or corrupt cover must never take the window down.
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
