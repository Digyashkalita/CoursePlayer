using Microsoft.Extensions.Logging;

namespace CoursePlayer.Services;

/// <summary>
/// Turns dropped folders and files into a set of proposed courses. Pure and UI-free: it
/// only reads the filesystem and returns a <see cref="ScanResult"/> for the wizard to edit.
/// </summary>
public interface IFolderScanner
{
    Task<ScanResult> ScanAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IFolderScanner"/>
public sealed class FolderScanner : IFolderScanner
{
    private readonly ILogger<FolderScanner> _logger;

    public FolderScanner(ILogger<FolderScanner> logger) => _logger = logger;

    public Task<ScanResult> ScanAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        // Enumerating a large or networked tree is blocking I/O, so keep it off the UI thread.
        Task.Run(() => Scan(paths, cancellationToken), cancellationToken);

    private ScanResult Scan(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var courses = new List<DetectedCourse>();
        var looseFiles = new List<string>();

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (Directory.Exists(path))
                {
                    ScanFolder(path, courses, cancellationToken);
                }
                else if (File.Exists(path) && FileTypeRegistry.IsSupported(path))
                {
                    looseFiles.Add(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A single unreadable path must not sink the whole scan.
                _logger.LogWarning(ex, "Skipping unreadable import path {Path}.", path);
            }
        }

        if (looseFiles.Count > 0)
        {
            courses.Add(BuildLooseFileCourse(looseFiles));
        }

        _logger.LogInformation(
            "Scan of {PathCount} path(s) produced {CourseCount} proposed course(s).",
            paths.Count,
            courses.Count);

        return new ScanResult(courses);
    }

    /// <summary>
    /// A dropped folder becomes a single course. Every media file anywhere under it becomes
    /// an asset, tagged with its folder path relative to the root so the imported hierarchy
    /// survives as sections (subfolders no longer split into separate courses).
    /// </summary>
    private void ScanFolder(string root, List<DetectedCourse> courses, CancellationToken cancellationToken)
    {
        var rootName = GetFolderName(root);
        var rootTrimmed = Path.TrimEndingDirectorySeparator(root);

        var mediaFiles = new List<string>();
        foreach (var directory in EnumerateDirectoriesInclusive(root, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            mediaFiles.AddRange(SafeEnumerateFiles(directory).Where(FileTypeRegistry.IsSupported));
        }

        if (mediaFiles.Count == 0)
        {
            return;
        }

        var assets = BuildAssets(mediaFiles, file => GetSection(rootTrimmed, file));
        courses.Add(new DetectedCourse(rootName, root, assets));
    }

    /// <summary>The containing folder's path relative to the course root, or null for root-level files.</summary>
    private static string? GetSection(string rootTrimmed, string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var directoryTrimmed = Path.TrimEndingDirectorySeparator(directory);
        if (string.Equals(directoryTrimmed, rootTrimmed, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Store with forward slashes so nested sections read the same everywhere.
        var relative = Path.GetRelativePath(rootTrimmed, directoryTrimmed);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static DetectedCourse BuildLooseFileCourse(List<string> files)
    {
        // Group loose files under whatever directory they share (their common parent).
        var parent = Path.GetDirectoryName(files[0]) ?? string.Empty;
        var title = string.IsNullOrEmpty(parent) ? "Imported files" : GetFolderName(parent);
        return new DetectedCourse(title, parent, BuildAssets(files, _ => null));
    }

    private static IReadOnlyList<DetectedAsset> BuildAssets(
        IEnumerable<string> files,
        Func<string, string?> sectionSelector)
    {
        // Sort by section first so subfolders stay in tree order, then by filename within a
        // section (so "Lesson 2" precedes "Lesson 10"). Root-level files (null section) lead.
        var ordered = files
            .Select(file => (File: file, Section: sectionSelector(file)))
            .OrderBy(x => x.Section ?? string.Empty, NaturalStringComparer.Instance)
            .ThenBy(x => Path.GetFileName(x.File), NaturalStringComparer.Instance)
            .ToList();

        var assets = new List<DetectedAsset>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var (file, section) = ordered[i];
            assets.Add(new DetectedAsset(
                Title: Path.GetFileNameWithoutExtension(file),
                FilePath: file,
                // Non-null: only supported files reach here.
                Type: FileTypeRegistry.Classify(file)!.Value,
                OrderIndex: i,
                Section: section));
        }

        return assets;
    }

    /// <summary>The root itself, then every descendant directory.</summary>
    private IEnumerable<string> EnumerateDirectoriesInclusive(string root, CancellationToken cancellationToken)
    {
        yield return root;

        IEnumerator<string> enumerator;
        try
        {
            enumerator = Directory
                .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not enumerate subfolders of {Root}.", root);
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Stopped enumerating subfolders of {Root}.", root);
                    break;
                }

                yield return enumerator.Current;
            }
        }
    }

    private IEnumerable<string> SafeEnumerateFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not list files in {Directory}.", directory);
            return [];
        }
    }

    private static string GetFolderName(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        var name = Path.GetFileName(trimmed);
        // A drive root ("D:\") has no file name; fall back to the path itself.
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}
