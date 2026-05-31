using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.InteropServices;
using API.Workers;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using static System.IO.UnixFileMode;

namespace API.Schema.SeriesContext;

[PrimaryKey("Key")]
[Table("Mangas")] // Existing DB table; a follow-up hand-crafted migration is needed to rename to "Series" (see TECHNICAL_DEBT.md).
public class Series : Identifiable
{
    [StringLength(512)] public string Name { get; internal set; }
    [Required] public string Description { get; internal set; }
    [Url] [StringLength(512)] public string CoverUrl { get; internal set; }
    public SeriesReleaseStatus ReleaseStatus { get; internal set; }
    [StringLength(64)] public string? LibraryId { get; private set; }
    public FileLibrary? Library = null!;
    public ICollection<Author> Authors { get; internal set; } = null!;
    public ICollection<SeriesTag> MangaTags { get; internal set; } = null!;
    public ICollection<Link> Links { get; internal set; } = null!;
    public ICollection<AltTitle> AltTitles { get; internal set; } = null!;
    public float IgnoreChaptersBefore { get; internal set; }
    [StringLength(1024)] [Required] public string DirectoryName { get; private set; }
    [StringLength(512)] public string? CoverFileNameInCache { get; internal set; }
    public uint? Year { get; internal init; }
    [StringLength(8)] public string? OriginalLanguage { get; internal init; }
    public bool IsTracked { get; internal set; }

    /// <summary>File layout preference for this manga's chapters on disk.</summary>
    public LibraryLayout LibraryLayout { get; internal set; } = LibraryLayout.Flat;

    /// <summary>Optional metadata source linkage for this manga.</summary>
    public MetadataSource? MetadataSource { get; internal set; }


    /// <exception cref="DirectoryNotFoundException">Library not loaded</exception>
    [NotMapped]
    [JsonIgnore]
    public string FullDirectoryPath => Library is not null ? Path.Join(Library.BasePath, DirectoryName) : throw new DirectoryNotFoundException("Library not loaded");

    [NotMapped]
    public ICollection<string> ChapterIds => Chapters.Select(c => c.Key).ToList();
    [JsonIgnore]
    public ICollection<Chapter> Chapters = null!;

    [NotMapped]
    public Dictionary<string, string> IdsOnMangaConnectors => SourceIds.ToDictionary(id => id.MangaConnectorName, id => id.IdOnConnectorSite);
    [NotMapped]
    public ICollection<string> SourceIdsIds => SourceIds.Select(id => id.Key).ToList();
    [JsonIgnore]
    public ICollection<SourceId<Series>> SourceIds = null!;

    public Series(string name, string description, string coverUrl, SeriesReleaseStatus releaseStatus,
        ICollection<Author> authors, ICollection<SeriesTag> mangaTags, ICollection<Link> links, ICollection<AltTitle> altTitles,
        FileLibrary? library = null, float ignoreChaptersBefore = 0f, uint? year = null, string? originalLanguage = null)
    :base(TokenGen.CreateToken(typeof(Series), name))
    {
        this.Name = name;
        this.Description = description;
        this.CoverUrl = coverUrl;
        this.ReleaseStatus = releaseStatus;
        this.Library = library;
        this.Authors = authors;
        this.MangaTags = mangaTags;
        this.Links = links;
        this.AltTitles = altTitles;
        this.IgnoreChaptersBefore = ignoreChaptersBefore;
        this.DirectoryName = name.CleanNameForWindows();
        this.Year = year;
        this.OriginalLanguage = originalLanguage;
        this.Chapters = [];
        this.SourceIds = [];
        this.MetadataSource = new MetadataSource(this.Key, MetadataSourceType.Connector, MetadataSourceStatus.Unlinked);
    }

    /// <summary>
    /// EF ONLY!!!
    /// </summary>
    public Series(string key, string name, string description, string coverUrl,
        SeriesReleaseStatus releaseStatus,
        string directoryName, float ignoreChaptersBefore, string? libraryId, uint? year, string? originalLanguage,
        LibraryLayout libraryLayout = LibraryLayout.Flat)
        : base(key)
    {
        this.Name = name;
        this.Description = description;
        this.CoverUrl = coverUrl;
        this.ReleaseStatus = releaseStatus;
        this.DirectoryName = directoryName;
        this.LibraryId = libraryId;
        this.IgnoreChaptersBefore = ignoreChaptersBefore;
        this.Year = year;
        this.OriginalLanguage = originalLanguage;
        this.LibraryLayout = libraryLayout;
    }
    
    /// <exception cref="DirectoryNotFoundException">Library not loaded</exception>
    public string EnsureDirectoryExists()
    {
        if (!Directory.Exists(FullDirectoryPath))
            Directory.CreateDirectory(FullDirectoryPath);
        return FullDirectoryPath;
    }

    /// <summary>
    /// Merges another Series (SourceIds and Chapters)
    /// </summary>
    /// <param name="other">The other <see cref="Series" /> to merge</param>
    /// <param name="context"><see cref="SeriesContext"/> to use for Database operations</param>
    /// <returns>An array of <see cref="MoveFileOrFolderWorker"/> for moving <see cref="Chapter"/> to new Directory</returns>
    public BaseWorker[] MergeFrom(Series other, SeriesContext context)
    {
        context.Series.Remove(other);
        List<BaseWorker> newJobs = new();

        this.SourceIds = this.SourceIds
            .UnionBy(other.SourceIds, id => id.MangaConnectorName)
            .ToList();

        foreach (Chapter otherChapter in other.Chapters)
        {
            if (otherChapter.FullArchiveFilePath is not { } oldPath)
                continue;
            Chapter newChapter = new(this, otherChapter.ChapterNumber, otherChapter.VolumeNumber,
                otherChapter.Title);
            this.Chapters.Add(newChapter);
            if (newChapter.FullArchiveFilePath is not { } newPath)
                continue;
            newJobs.Add(new MoveFileOrFolderWorker(newPath, oldPath));
        }
        
        return newJobs.ToArray();
    }

    public async Task<(MemoryStream stream, FileInfo fileInfo)?> GetCoverImage(string cachePath, CancellationToken ct)
    {
        string fullPath = Path.Join(cachePath, CoverFileNameInCache);
        if (!File.Exists(fullPath))
            return null;

        FileInfo fileInfo = new(fullPath);
        MemoryStream stream = new (await File.ReadAllBytesAsync(fullPath, ct));
        
        return (stream, fileInfo);
    }

    public override string ToString() => $"{base.ToString()} {Name}";
}

public enum SeriesReleaseStatus
{
    Continuing,
    Completed,
    OnHiatus,
    Cancelled,
    Unreleased
}