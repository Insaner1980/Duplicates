using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Duplicates.Engine.Analysis;

namespace Duplicates.ViewModels;

public sealed partial class SimilarityItemViewModel : ObservableObject
{
    private readonly Action _selectionChanged;

    public SimilarityItemViewModel(SimilarityItem source, bool isReference, Action selectionChanged)
    {
        Source = source;
        IsReference = isReference;
        _selectionChanged = selectionChanged;
    }

    public SimilarityItem Source { get; }

    public bool IsReference { get; }

    public string FullPath => Source.FullPath;

    public string DisplayName => Path.GetFileName(FullPath);

    public long SizeBytes => Source.SizeBytes;

    public DateTime ModifiedUtc => Source.ModifiedUtc;

    public string SimilarityText => $"{Source.SimilarityPercent:N0}% similar";

    public string MediaDetailsText => Source.Evidence switch
    {
        ImageSimilarityEvidence image => $"{image.Width:N0} × {image.Height:N0}, {image.Format}",
        _ => string.Empty,
    };

    public string SummaryText => $"{SimilarityText}, {ByteFormatter.Format(SizeBytes)}, modified {ModifiedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}";

    public string MetadataText => string.Join(
        Environment.NewLine,
        Source.Metadata.Select(static pair => $"{pair.Key}: {pair.Value}"));

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => _selectionChanged();
}
