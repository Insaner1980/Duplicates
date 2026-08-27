using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Duplicates.Engine.Analysis;

namespace Duplicates.ViewModels;

public sealed partial class PathFindingViewModel : ObservableObject
{
    private readonly Action _selectionChanged;

    public PathFindingViewModel(PathFinding source, Action selectionChanged)
    {
        Source = source;
        _selectionChanged = selectionChanged;
    }

    public PathFinding Source { get; }

    public string FullPath => Source.FullPath;

    public string DisplayName => Path.GetFileName(Path.TrimEndingDirectorySeparator(FullPath));

    public string Reason => Source.Reason;

    public string Suggestion => Source.Suggestion ?? string.Empty;

    public long SizeBytes => Source.SizeBytes ?? 0;

    public DateTime? ModifiedUtc => Source.ModifiedUtc;

    public string SummaryText => string.Join(
        ", ",
        new[] { Reason, Source.SizeBytes is null ? null : ByteFormatter.Format(Source.SizeBytes.Value), Suggestion }
            .Where(static value => !string.IsNullOrWhiteSpace(value)));

    public string MetadataText => string.Join(
        Environment.NewLine,
        Source.Metadata.Select(static pair => $"{pair.Key}: {pair.Value}"));

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();
}
