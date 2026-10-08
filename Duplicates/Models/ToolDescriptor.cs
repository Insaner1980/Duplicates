namespace Duplicates.Models;

public sealed record ToolDescriptor(
    ToolKind Kind,
    string Title,
    string Subtitle,
    string NavigationGroup)
{
    private const string FindDuplicatesGroup = "Find duplicates";
    private const string CleanStorageGroup = "Clean storage";
    private const string InspectAndRepairGroup = "Inspect and repair";

    public static ToolDescriptor For(ToolKind kind) => kind switch
    {
        ToolKind.DuplicateFiles => new(kind, "Duplicate files", "Find byte-identical files.", FindDuplicatesGroup),
        ToolKind.SimilarImages => new(kind, "Similar images", "Find visually similar images.", FindDuplicatesGroup),
        ToolKind.SimilarVideos => new(kind, "Similar videos", "Find visually similar videos.", FindDuplicatesGroup),
        ToolKind.MusicDuplicates => new(
            kind,
            "Music duplicates",
            "Match tracks using Windows music metadata and duration, not acoustic fingerprinting.",
            FindDuplicatesGroup),
        ToolKind.EmptyFolders => new(kind, "Empty folders", "Find folders with no contents.", CleanStorageGroup),
        ToolKind.BigFiles => new(kind, "Big files", "Find files that use the most space.", CleanStorageGroup),
        ToolKind.EmptyFiles => new(kind, "Empty files", "Find zero-byte files.", CleanStorageGroup),
        ToolKind.TemporaryFiles => new(kind, "Temporary files", "Find old temporary files.", CleanStorageGroup),
        ToolKind.InvalidLinks => new(kind, "Invalid links", "Find broken symbolic links and junctions.", InspectAndRepairGroup),
        ToolKind.BrokenFiles => new(kind, "Broken files", "Checks readability and validates Windows-supported images, audio, video, and ZIP containers.", InspectAndRepairGroup),
        ToolKind.BadExtensions => new(kind, "Bad extensions", "Find extensions that do not match file contents.", InspectAndRepairGroup),
        ToolKind.BadNames => new(kind, "Bad names", "Find file names that are unsafe on Windows.", InspectAndRepairGroup),
        ToolKind.ExifRemover => new(kind, "EXIF remover", "Remove private metadata from images.", InspectAndRepairGroup),
        ToolKind.VideoOptimizer => new(kind, "Video optimizer", "Reduce video file size.", InspectAndRepairGroup),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
