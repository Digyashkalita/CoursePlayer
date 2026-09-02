using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CoursePlayer.Services;

namespace CoursePlayer.ViewModels;

/// <summary>
/// Backs the import wizard dialog. Presents the scanned courses for review and, on confirm,
/// exposes the ticked/renamed set as an <see cref="ImportWizardResult"/>.
/// </summary>
public partial class ImportWizardViewModel : ObservableObject
{
    public ImportWizardViewModel(ScanResult scan)
    {
        Courses = new ObservableCollection<DetectedCourseViewModel>(
            scan.Courses.Select(c => new DetectedCourseViewModel(c)));

        // The confirm button's enablement and label track how many rows stay ticked.
        Courses.CollectionChanged += OnCoursesChanged;
        foreach (var course in Courses)
        {
            course.PropertyChanged += OnCoursePropertyChanged;
        }
    }

    public ObservableCollection<DetectedCourseViewModel> Courses { get; }

    /// <summary>Set to the confirmed set when the user clicks Import; null while pending/cancelled.</summary>
    public ImportWizardResult? Result { get; private set; }

    /// <summary>True after Confirm/Cancel so the window knows how to close.</summary>
    public bool? DialogResult { get; private set; }

    /// <summary>Raised when the dialog should close; the window subscribes and calls Close().</summary>
    public event EventHandler? RequestClose;

    public int SelectedCount => Courses.Count(c => c.IsSelected);

    public string ConfirmLabel =>
        SelectedCount == 1 ? "Import 1 course" : $"Import {SelectedCount} courses";

    public string HeaderText => Courses.Count == 1
        ? "Found 1 course"
        : $"Found {Courses.Count} courses";

    public bool CanConfirm => SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        var confirmed = Courses
            .Where(c => c.IsSelected)
            .Select(c => new ConfirmedCourse(
                Title: string.IsNullOrWhiteSpace(c.Title) ? "Untitled course" : c.Title.Trim(),
                FolderPath: c.FolderPath,
                Assets: c.Assets))
            .ToList();

        Result = new ImportWizardResult(confirmed);
        DialogResult = true;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        DialogResult = false;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private void OnCoursesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (DetectedCourseViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnCoursePropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (DetectedCourseViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnCoursePropertyChanged;
            }
        }

        RaiseSelectionChanged();
    }

    private void OnCoursePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DetectedCourseViewModel.IsSelected))
        {
            RaiseSelectionChanged();
        }
    }

    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(ConfirmLabel));
        OnPropertyChanged(nameof(CanConfirm));
        ConfirmCommand.NotifyCanExecuteChanged();
    }
}
