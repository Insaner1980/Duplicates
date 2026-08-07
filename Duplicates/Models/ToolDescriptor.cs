namespace Duplicates.Models;

public sealed record ToolDescriptor(
    ToolKind Kind,
    string Title,
    string Subtitle,
    string NavigationGroup,
    string Glyph)
{
    public static ToolDescriptor For(ToolKind kind) => kind switch
    {
        ToolKind.DuplicateFiles => new(kind, "Duplicate files", "Find byte-identical files.", "Find duplicates", "\uE8C8"),
        ToolKind.SimilarImages => new(kind, "Similar images", "Find visually similar images.", "Find duplicates", "\uEB9F"),
        ToolKind.SimilarVideos => new(kind, "Similar videos", "Find visually similar videos.", "Find duplicates", "\uE714"),
        ToolKind.MusicDuplicates => new(kind, "Music duplicates", "Find matching music by metadata.", "Find duplicates", "\uE8D6"),
        ToolKind.EmptyFolders => new(kind, "Empty folders", "Find folders with no contents.", "Clean storage", "\uE8B7"),
        ToolKind.BigFiles => new(kind, "Big files", "Find files that use the most space.", "Clean storage", "\uE8A5"),
        ToolKind.EmptyFiles => new(kind, "Empty files", "Find zero-byte files.", "Clean storage", "\uE8A5"),
        ToolKind.TemporaryFiles => new(kind, "Temporary files", "Find old temporary files.", "Clean storage", "\uE823"),
        ToolKind.InvalidLinks => new(kind, "Invalid links", "Find broken symbolic links and junctions.", "Inspect and repair", "\uE71B"),
        ToolKind.BrokenFiles => new(kind, "Broken files", "Find unreadable or malformed files.", "Inspect and repair", "\uE7BA"),
        ToolKind.BadExtensions => new(kind, "Bad extensions", "Find extensions that do not match file contents.", "Inspect and repair", "\uE8C1"),
        ToolKind.BadNames => new(kind, "Bad names", "Find file names that are unsafe on Windows.", "Inspect and repair", "\uE8AC"),
        ToolKind.ExifRemover => new(kind, "EXIF remover", "Remove private metadata from images.", "Inspect and repair", "\uE722"),
        ToolKind.VideoOptimizer => new(kind, "Video optimizer", "Reduce video file size.", "Inspect and repair", "\uE74D"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
