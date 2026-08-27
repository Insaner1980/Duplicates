using System.Collections.ObjectModel;
using Duplicates.Engine.Analysis;

namespace Duplicates.ViewModels;

public sealed class SimilarityGroupViewModel
{
    public SimilarityGroupViewModel(SimilarityGroup source, Action selectionChanged)
    {
        Source = source;
        Items = new ObservableCollection<SimilarityItemViewModel>(
            source.Items.Select(item => new SimilarityItemViewModel(
                item,
                ReferenceEquals(item, source.ReferenceItem),
                selectionChanged)));
    }

    public SimilarityGroup Source { get; }

    public string Id => Source.Id;

    public ObservableCollection<SimilarityItemViewModel> Items { get; }

    public SimilarityItemViewModel ReferenceItem => Items.First(item => item.IsReference);

    public string FullPath => ReferenceItem.FullPath;

    public string DisplayName => Source.ReferenceItem.Evidence is MusicSimilarityEvidence
        ? $"{Items.Count:N0} matching tracks"
        : $"{Items.Count:N0} similar items";

    public string SummaryText
    {
        get
        {
            int candidateCount = Math.Max(0, Items.Count - 1);
            string referencePrefix = Source.ReferenceItem.Evidence is MusicSimilarityEvidence
                ? "High confidence, reference"
                : "Reference";
            return $"{referencePrefix}: {ReferenceItem.DisplayName}, {candidateCount:N0} " +
                (candidateCount == 1 ? "candidate" : "candidates");
        }
    }

    public string MetadataText => string.Join(
        Environment.NewLine,
        Source.Metadata.Select(static pair => $"{pair.Key}: {pair.Value}"));

    public int SelectedCount => Items.Count(static item => item.IsSelected);

    public long SelectedBytes => Items.Where(static item => item.IsSelected).Sum(static item => item.SizeBytes);
}
