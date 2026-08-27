using System.Globalization;
using System.Text;
using System.Text.Json;
using Duplicates.Engine.Models;

namespace Duplicates.Services;

public sealed class ResultExportService : IResultExportService
{
    private const string CsvHeader = "record_type,tool,generated_utc,scope_summary,path,kind,reason,suggestion,group_id,similarity_percent,size_bytes,created_utc,modified_utc,metadata,skipped_reason";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public async Task ExportAsync(
        ResultExportSnapshot snapshot,
        ResultExportFormat format,
        string destinationPath,
        CancellationToken cancellationToken,
        bool overwriteExisting = false)
    {
        ResultExportSnapshot copy = CopySnapshot(snapshot);
        string destination = ValidateDestination(destinationPath);
        if (Directory.Exists(destination) || (!overwriteExisting && File.Exists(destination)))
        {
            throw new IOException("The export destination already exists.");
        }

        string parent = Path.GetDirectoryName(destination)!;
        string temporaryPath = Path.Combine(
            parent,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                65_536,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (format == ResultExportFormat.Csv)
                {
                    await WriteCsvAsync(copy, stream, cancellationToken);
                }
                else
                {
                    await WriteJsonAsync(copy, stream, cancellationToken);
                }

                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: overwriteExisting);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task WriteCsvAsync(
        ResultExportSnapshot snapshot,
        Stream stream,
        CancellationToken cancellationToken)
    {
        string generatedUtc = FormatUtc(snapshot.GeneratedAtUtc);
        var rows = new List<(string GroupId, string Path, string Row)>();
        foreach (ResultExportItem item in SortItems(snapshot.Items))
        {
            string metadata = JsonSerializer.Serialize(
                item.Metadata.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal));
            rows.Add((
                item.GroupId ?? string.Empty,
                item.FullPath,
                CsvRow(
                    "item",
                    snapshot.Tool.ToString(),
                    generatedUtc,
                    snapshot.ScopeSummary,
                    item.FullPath,
                    item.Kind.ToString(),
                    item.Reason,
                    item.Suggestion,
                    item.GroupId,
                    item.SimilarityPercent?.ToString("R", CultureInfo.InvariantCulture),
                    item.SizeBytes?.ToString(CultureInfo.InvariantCulture),
                    FormatUtc(item.CreatedUtc),
                    FormatUtc(item.ModifiedUtc),
                    metadata,
                    null)));
        }

        foreach (SkippedPath skipped in snapshot.SkippedPaths)
        {
            rows.Add((
                string.Empty,
                skipped.Path,
                CsvRow(
                    "skipped_path",
                    snapshot.Tool.ToString(),
                    generatedUtc,
                    snapshot.ScopeSummary,
                    skipped.Path,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    "{}",
                    skipped.Reason)));
        }

        rows.Sort(static (left, right) => CompareRecords(left.GroupId, left.Path, right.GroupId, right.Path));
        var content = new StringBuilder(CsvHeader.Length + (rows.Count * 256));
        content.Append(CsvHeader).Append("\r\n");
        foreach ((_, _, string row) in rows)
        {
            content.Append(row).Append("\r\n");
        }

        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true)
        {
            NewLine = "\r\n",
        };
        await writer.WriteAsync(content.ToString().AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static Task WriteJsonAsync(
        ResultExportSnapshot snapshot,
        Stream stream,
        CancellationToken cancellationToken)
    {
        object output = new
        {
            metadata = new
            {
                tool = snapshot.Tool.ToString(),
                generatedAtUtc = FormatUtc(snapshot.GeneratedAtUtc),
                scopeSummary = snapshot.ScopeSummary,
            },
            items = SortItems(snapshot.Items).Select(static item => new
            {
                fullPath = item.FullPath,
                kind = item.Kind.ToString(),
                reason = item.Reason,
                suggestion = item.Suggestion,
                groupId = item.GroupId,
                similarityPercent = item.SimilarityPercent,
                sizeBytes = item.SizeBytes,
                createdUtc = FormatUtc(item.CreatedUtc),
                modifiedUtc = FormatUtc(item.ModifiedUtc),
                metadata = item.Metadata.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
            }).ToArray(),
            skippedPaths = snapshot.SkippedPaths
                .OrderBy(static skipped => skipped.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static skipped => skipped.Path, StringComparer.Ordinal)
                .Select(static skipped => new
                {
                    path = skipped.Path,
                    reason = skipped.Reason,
                })
                .ToArray(),
        };

        return JsonSerializer.SerializeAsync(stream, output, JsonOptions, cancellationToken);
    }

    private static ResultExportSnapshot CopySnapshot(ResultExportSnapshot snapshot)
    {
        ResultExportItem[] items = snapshot.Items.Select(static item => item with
        {
            Metadata = item.Metadata.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal),
        }).ToArray();
        SkippedPath[] skippedPaths = snapshot.SkippedPaths.Select(static skipped => new SkippedPath
        {
            Path = skipped.Path,
            Reason = skipped.Reason,
        }).ToArray();
        return snapshot with
        {
            GeneratedAtUtc = snapshot.GeneratedAtUtc.ToUniversalTime(),
            Items = items,
            SkippedPaths = skippedPaths,
        };
    }

    private static string ValidateDestination(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath) || !Path.IsPathFullyQualified(destinationPath))
        {
            throw new ArgumentException("The export destination must be an absolute path.", nameof(destinationPath));
        }

        string destination = Path.GetFullPath(destinationPath);
        string? parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            throw new ArgumentException("The export destination folder does not exist.", nameof(destinationPath));
        }

        return destination;
    }

    private static IOrderedEnumerable<ResultExportItem> SortItems(IReadOnlyList<ResultExportItem> items) =>
        items.OrderBy(static item => item.GroupId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.GroupId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.FullPath, StringComparer.Ordinal);

    private static int CompareRecords(
        string leftGroup,
        string leftPath,
        string rightGroup,
        string rightPath)
    {
        int comparison = StringComparer.OrdinalIgnoreCase.Compare(leftGroup, rightGroup);
        if (comparison == 0)
        {
            comparison = StringComparer.Ordinal.Compare(leftGroup, rightGroup);
        }

        if (comparison == 0)
        {
            comparison = StringComparer.OrdinalIgnoreCase.Compare(leftPath, rightPath);
        }

        return comparison == 0 ? StringComparer.Ordinal.Compare(leftPath, rightPath) : comparison;
    }

    private static string CsvRow(params string?[] values) =>
        string.Join(',', values.Select(static value => EscapeCsv(value ?? string.Empty)));

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;

    private static string? FormatUtc(DateTime? value) =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
