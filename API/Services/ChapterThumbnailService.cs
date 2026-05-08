using System.IO.Compression;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace API.Services;

/// <summary>
/// Compares filename strings using natural (numeric-aware) ordering so that
/// "page_9.jpg" sorts before "page_10.jpg".
/// </summary>
public sealed class NaturalSortComparer : IComparer<string>
{
    public static readonly NaturalSortComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null && y is null) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            bool xDigit = char.IsDigit(x[ix]);
            bool yDigit = char.IsDigit(y[iy]);

            if (xDigit && yDigit)
            {
                // Parse both numeric runs
                int xStart = ix, yStart = iy;
                while (ix < x.Length && char.IsDigit(x[ix])) ix++;
                while (iy < y.Length && char.IsDigit(y[iy])) iy++;

                // Compare as integers (trim leading zeros by parsing)
                long xNum = long.Parse(x.AsSpan(xStart, ix - xStart));
                long yNum = long.Parse(y.AsSpan(yStart, iy - yStart));

                int cmp = xNum.CompareTo(yNum);
                if (cmp != 0) return cmp;
            }
            else
            {
                int cmp = char.ToLowerInvariant(x[ix]).CompareTo(char.ToLowerInvariant(y[iy]));
                if (cmp != 0) return cmp;
                ix++;
                iy++;
            }
        }

        return (x.Length - ix).CompareTo(y.Length - iy);
    }
}

/// <inheritdoc cref="IChapterThumbnailService"/>
public sealed class ChapterThumbnailService : IChapterThumbnailService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    /// <inheritdoc/>
    public async Task<bool> GenerateThumbnailAsync(
        string archivePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);

            var imageEntry = archive.Entries
                .Where(e => ImageExtensions.Contains(Path.GetExtension(e.FullName)))
                .OrderBy(e =>
                    // cover.* (case-insensitive) sorts first; everything else second
                    Path.GetFileNameWithoutExtension(e.FullName)
                        .Equals("cover", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(e => e.FullName, NaturalSortComparer.Instance)
                .FirstOrDefault();

            if (imageEntry is null)
                return false;

            using var entryStream = imageEntry.Open();
            using var image = await Image.LoadAsync(entryStream, cancellationToken);

            image.Mutate(ctx => ctx.Resize(200, 300));

            string? directory = Path.GetDirectoryName(destinationPath);
            if (directory is not null)
                Directory.CreateDirectory(directory);

            await image.SaveAsJpegAsync(destinationPath, cancellationToken);
            return true;
        }
        catch (Exception)
        {
            // Unreadable archive, corrupt image, etc. — return false so caller returns 404
            return false;
        }
    }
}
