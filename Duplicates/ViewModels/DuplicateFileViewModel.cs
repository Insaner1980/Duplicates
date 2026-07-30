using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Duplicates.Engine.Models;
using Microsoft.UI.Xaml;

namespace Duplicates.ViewModels;

public sealed class DuplicateFileViewModel : ObservableObject
{
    private readonly DuplicateGroupViewModel _parent;
    private bool _isSelected;

    public DuplicateFileViewModel(FileEntry file, DuplicateGroupViewModel parent)
    {
        File = file;
        _parent = parent;
    }

    public FileEntry File { get; }

    public string FullPath => File.FullPath;

    public string DisplayDirectoryPath => ShortenMiddle(DirectoryPath);

    public string FileName => File.FileName;

    public string DirectoryPath => File.DirectoryPath;

    public string Extension => File.Extension;

    public long SizeBytes => File.SizeBytes;

    public string SizeText => ByteFormatter.Format(SizeBytes);

    public string ModifiedText => File.ModifiedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string CreatedText => File.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string MetadataText => $"Modified {ModifiedText}, Created {CreatedText}, {SizeText}";

    public bool IsKept => !IsSelected;

    public Visibility KeptVisibility => IsKept ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DeleteVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            if (value && !_parent.CanSelectForDeletion(this))
            {
                OnPropertyChanged();
                return;
            }

            SetSelectedCore(value);
            _parent.NotifySelectionChanged();
        }
    }

    internal void SetSelectedFromRule(bool value)
    {
        SetSelectedCore(value);
    }

    internal void NotifyKeptChanged()
    {
        OnPropertyChanged(nameof(IsKept));
        OnPropertyChanged(nameof(KeptVisibility));
        OnPropertyChanged(nameof(DeleteVisibility));
    }

    private void SetSelectedCore(bool value)
    {
        if (SetProperty(ref _isSelected, value, nameof(IsSelected)))
        {
            OnPropertyChanged(nameof(IsKept));
            OnPropertyChanged(nameof(KeptVisibility));
            OnPropertyChanged(nameof(DeleteVisibility));
        }
    }

    private static string ShortenMiddle(string path)
    {
        const int maxLength = 72;
        const int headLength = 28;
        const int tailLength = 36;

        if (path.Length <= maxLength)
        {
            return path;
        }

        string fileName = Path.GetFileName(path);
        int tailStart = Math.Max(path.Length - Math.Max(tailLength, fileName.Length), 0);
        return string.Concat(path.AsSpan(0, headLength), "...", path.AsSpan(tailStart));
    }
}
