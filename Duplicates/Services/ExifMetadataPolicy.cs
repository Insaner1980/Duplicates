namespace Duplicates.Services;

internal static class ExifMetadataPolicy
{
    public static IReadOnlyList<string> AllQueries(WicContainerKind container) =>
        SelectedQueries(container, new ExifCleanOptions(true, true, true, true, true, true, false));

    public static IReadOnlyList<string> SelectedQueries(
        WicContainerKind container,
        ExifCleanOptions options)
    {
        string ifd = container == WicContainerKind.Jpeg ? "/app1/ifd" : "/ifd";
        string exif = $"{ifd}/exif";
        var queries = new List<string>();

        if (options.RemoveGps)
        {
            queries.Add($"{ifd}/gps");
        }

        if (options.RemoveDeviceIdentifiers)
        {
            AddTags(queries, ifd, [271, 272]);
            AddTags(queries, exif, [37500, 42016, 42032, 42033, 42035, 42036, 42037]);
        }

        if (options.RemoveDates)
        {
            AddTags(queries, ifd, [306]);
            AddTags(queries, exif, [36867, 36868, 36880, 36881, 36882, 37520, 37521, 37522]);
        }

        if (options.RemoveAuthorAndDescription)
        {
            AddTags(queries, ifd, [270, 315, 33432, 40091, 40092, 40093, 40094, 40095]);
            AddTags(queries, exif, [37510]);
            if (container == WicContainerKind.Jpeg)
            {
                queries.Add("/com");
            }
        }

        if (options.RemoveEmbeddedThumbnail)
        {
            queries.Add(container == WicContainerKind.Jpeg ? "/app1/thumb" : "/ifd/thumb");
        }

        if (options.RemoveXmpAndIptc)
        {
            if (container == WicContainerKind.Jpeg)
            {
                queries.Add("/xmp");
                queries.Add("/app13/irb/8bimiptc/iptc");
            }
            else
            {
                queries.Add("/ifd/xmp");
                queries.Add("/ifd/iptc");
                queries.Add("/ifd/irb/8bimiptc/iptc");
            }
        }

        return queries;
    }

    private static void AddTags(List<string> queries, string prefix, IReadOnlyList<int> tags)
    {
        foreach (int tag in tags)
        {
            queries.Add($"{prefix}/{{ushort={tag}}}");
        }
    }
}
