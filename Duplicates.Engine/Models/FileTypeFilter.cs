namespace Duplicates.Engine.Models;

public sealed record FileTypeFilter
{
    private static readonly IReadOnlyDictionary<FileTypeCategory, IReadOnlySet<string>> CategoryExtensions =
        new Dictionary<FileTypeCategory, IReadOnlySet<string>>
        {
            [FileTypeCategory.Images] = ToSet("jpg", "jpeg", "png", "gif", "bmp", "tiff", "tif", "webp", "heic", "heif", "raw", "cr2", "nef", "arw", "dng", "svg", "ico", "psd"),
            [FileTypeCategory.Video] = ToSet("mp4", "mkv", "mov", "avi", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "3gp", "ts"),
            [FileTypeCategory.Audio] = ToSet("mp3", "flac", "wav", "aac", "ogg", "m4a", "wma", "opus", "aiff", "alac"),
            [FileTypeCategory.Documents] = ToSet("pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "odt", "ods", "odp", "epub", "md", "csv"),
            [FileTypeCategory.Archives] = ToSet("zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab"),
            [FileTypeCategory.Code] = ToSet("cs", "js", "ts", "py", "java", "kt", "cpp", "h", "html", "css", "json", "xml", "yml", "sql"),
        };

    public static FileTypeFilter All { get; } = new()
    {
        Mode = FileTypeFilterMode.All,
        Categories = new HashSet<FileTypeCategory>(),
        Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    };

    public FileTypeFilterMode Mode { get; init; } = FileTypeFilterMode.All;

    public IReadOnlySet<FileTypeCategory> Categories { get; init; } = new HashSet<FileTypeCategory>();

    public IReadOnlySet<string> Extensions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static FileTypeFilter ForCategories(IEnumerable<FileTypeCategory> categories)
    {
        return new FileTypeFilter
        {
            Mode = FileTypeFilterMode.Categories,
            Categories = categories.ToHashSet(),
            Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };
    }

    public static FileTypeFilter ForCustomExtensions(IEnumerable<string> extensions)
    {
        return new FileTypeFilter
        {
            Mode = FileTypeFilterMode.CustomExtensions,
            Categories = new HashSet<FileTypeCategory>(),
            Extensions = extensions
                .Select(NormalizeExtension)
                .Where(static extension => extension.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
        };
    }

    public bool Matches(string extension)
    {
        if (Mode == FileTypeFilterMode.All)
        {
            return true;
        }

        string normalized = NormalizeExtension(extension);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (Mode == FileTypeFilterMode.CustomExtensions)
        {
            return Extensions.Contains(normalized);
        }

        return Categories
            .SelectMany(GetCategoryExtensions)
            .Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeExtension(string extension)
    {
        string trimmed = extension.Trim().TrimStart('*').Trim().ToLowerInvariant();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed[0] == '.' ? trimmed : "." + trimmed;
    }

    private static IEnumerable<string> GetCategoryExtensions(FileTypeCategory category)
    {
        return CategoryExtensions.TryGetValue(category, out IReadOnlySet<string>? extensions)
            ? extensions
            : Array.Empty<string>();
    }

    private static IReadOnlySet<string> ToSet(params string[] extensions)
    {
        return extensions.Select(NormalizeExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
