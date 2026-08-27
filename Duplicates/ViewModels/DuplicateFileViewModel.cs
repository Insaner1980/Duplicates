using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Duplicates.Engine.Models;
using Microsoft.UI.Xaml;

namespace Duplicates.ViewModels;

public sealed class DuplicateFileViewModel : ObservableObject
{
    private readonly DuplicateGroupViewModel _parent;
    private bool _isSelected;
    private bool _isLinkSurvivor;

    public DuplicateFileViewModel(FileEntry file, DuplicateGroupViewModel parent)
    {
        File = file;
        _parent = parent;
    }

    public FileEntry File { get; }

    public string FullPath => File.FullPath;

    public string DisplayDirectoryPath => DirectoryPath;

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

    public bool CanMutateSelection => _parent.CanMutateSelection;

    public bool CanToggleDeletionSelection =>
        CanMutateSelection && (IsSelected || _parent.CanSelectForDeletion(this));

    public bool CanBeLinkSurvivor => CanMutateSelection && !IsSelected;

    public bool IsLinkSurvivor
    {
        get => _isLinkSurvivor;
        set
        {
            if (_isLinkSurvivor == value)
            {
                return;
            }

            if (!CanMutateSelection)
            {
                OnPropertyChanged();
                return;
            }

            if (value)
            {
                if (!_parent.CanSetLinkSurvivor(this))
                {
                    OnPropertyChanged();
                    return;
                }

                _parent.SetLinkSurvivor(this);
            }
            else if (_isLinkSurvivor)
            {
                _parent.ClearLinkSurvivor(this);
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            if (!CanMutateSelection)
            {
                OnPropertyChanged();
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
        OnPropertyChanged(nameof(CanToggleDeletionSelection));
        OnPropertyChanged(nameof(CanBeLinkSurvivor));
    }

    internal void SetLinkSurvivorCore(bool value)
    {
        SetProperty(ref _isLinkSurvivor, value, nameof(IsLinkSurvivor));
    }

    internal void NotifyMutationAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanMutateSelection));
        OnPropertyChanged(nameof(CanToggleDeletionSelection));
        OnPropertyChanged(nameof(CanBeLinkSurvivor));
    }

    private void SetSelectedCore(bool value)
    {
        if (SetProperty(ref _isSelected, value, nameof(IsSelected)))
        {
            OnPropertyChanged(nameof(IsKept));
            OnPropertyChanged(nameof(KeptVisibility));
            OnPropertyChanged(nameof(DeleteVisibility));
            OnPropertyChanged(nameof(CanBeLinkSurvivor));
        }
    }
}
