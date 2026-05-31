using API.MangaConnectors;
using API.Schema.SeriesContext.MetadataFetchers;
using log4net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
// Inside this class the DbSet property `Series` shadows the entity type `Series`. SeriesEntity
// disambiguates in typeof/nameof expressions used by the entity configuration.
using SeriesEntity = API.Schema.SeriesContext.Series;

namespace API.Schema.SeriesContext;

public class SeriesContext(DbContextOptions<SeriesContext> options) : TrangaBaseContext<SeriesContext>(options)
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SeriesContext));
    public DbSet<Series> Series { get; set; }
    public DbSet<FileLibrary> FileLibraries { get; set; }
    public DbSet<Chapter> Chapters { get; set; }
    public DbSet<Author> Authors { get; set; }
    public DbSet<SeriesTag> Tags { get; set; }
    public DbSet<SourceId<Series>> MangaConnectorToManga { get; set; }
    public DbSet<SourceId<Chapter>> MangaConnectorToChapter { get; set; }
    public DbSet<MetadataEntry> MetadataEntries { get; set; }
    public DbSet<MetadataSource> MetadataSources { get; set; }
    public DbSet<VolumeMetadata> VolumeMetadata { get; set; }
    public DbSet<BundleChapterMap> BundleChapterMaps { get; set; }

    public IQueryable<Series> GetTrackedMangas() =>
        Series
            .Include(m => m.SourceIds)
            .Where(m => m.IsTracked
                        || m.SourceIds.Any(id => id.UseForDownload)
                        || Chapters.Any(c => c.ParentMangaId == m.Key && c.Downloaded));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        //Series has many Chapters
        modelBuilder.Entity<Series>()
            .HasMany<Chapter>(m => m.Chapters)
            .WithOne(c => c.ParentManga)
            .HasForeignKey(c => c.ParentMangaId)
            .OnDelete(DeleteBehavior.Cascade);
        //Chapter has SourceIds
        modelBuilder.Entity<Chapter>()
            .HasMany<SourceId<Chapter>>(c => c.SourceIds)
            .WithOne(id => id.Obj)
            .HasForeignKey(id => id.ObjId)
            .OnDelete(DeleteBehavior.Cascade);
        //Series owns MangaAltTitles
        modelBuilder.Entity<Series>()
            .OwnsMany<AltTitle>(m => m.AltTitles)
            .WithOwner();
        modelBuilder.Entity<Series>()
            .Navigation(m => m.AltTitles)
            .AutoInclude();
        //Series owns Links
        modelBuilder.Entity<Series>()
            .OwnsMany<Link>(m => m.Links)
            .WithOwner();
        modelBuilder.Entity<Series>()
            .Navigation(m => m.Links)
            .AutoInclude();
        //Series has many Tags associated with many Obj
        modelBuilder.Entity<Series>()
            .HasMany<SeriesTag>(m => m.MangaTags)
            .WithMany()
            .UsingEntity("SeriesTagToSeries",
                l => l.HasOne(typeof(SeriesTag)).WithMany().HasForeignKey("MangaTagIds")
                    .HasPrincipalKey(nameof(SeriesTag.Tag)),
                r => r.HasOne(typeof(SeriesEntity)).WithMany().HasForeignKey("MangaIds").HasPrincipalKey(nameof(SeriesEntity.Key)),
                j => j.HasKey("MangaTagIds", "MangaIds")
            );
        modelBuilder.Entity<Series>()
            .Navigation(m => m.MangaTags)
            .AutoInclude();
        //Series has many Authors associated with many Obj
        modelBuilder.Entity<Series>()
            .HasMany<Author>(m => m.Authors)
            .WithMany()
            .UsingEntity("AuthorToManga",
                l => l.HasOne(typeof(Author)).WithMany().HasForeignKey("AuthorIds").HasPrincipalKey(nameof(Author.Key)),
                r => r.HasOne(typeof(SeriesEntity)).WithMany().HasForeignKey("MangaIds").HasPrincipalKey(nameof(SeriesEntity.Key)),
                j => j.HasKey("AuthorIds", "MangaIds")
            );
        modelBuilder.Entity<Series>()
            .Navigation(m => m.Authors)
            .AutoInclude();
        //Series has many MangaIds
        modelBuilder.Entity<Series>()
            .HasMany<SourceId<Series>>(m => m.SourceIds)
            .WithOne(id => id.Obj)
            .HasForeignKey(id => id.ObjId)
            .OnDelete(DeleteBehavior.Cascade);


        //FileLibrary has many Series
        modelBuilder.Entity<FileLibrary>()
            .HasMany<Series>()
            .WithOne(m => m.Library)
            .HasForeignKey(m => m.LibraryId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<MetadataFetcher>()
            .HasDiscriminator<string>(nameof(MetadataEntry))
            .HasValue<MyAnimeList>(nameof(MyAnimeList));
        //MetadataEntry
        modelBuilder.Entity<MetadataEntry>()
            .HasOne<Series>(entry => entry.Series)
            .WithMany()
            .OnDelete(DeleteBehavior.Cascade);

        // Series has one MetadataSource
        modelBuilder.Entity<Series>()
            .HasOne<MetadataSource>(m => m.MetadataSource)
            .WithOne(ms => ms.Series)
            .HasForeignKey<MetadataSource>(ms => ms.MangaId)
            .OnDelete(DeleteBehavior.Cascade);

        // Series has many VolumeMetadata
        modelBuilder.Entity<VolumeMetadata>()
            .HasOne<Series>(v => v.Series)
            .WithMany()
            .HasForeignKey(v => v.MangaId)
            .OnDelete(DeleteBehavior.Cascade);

        // BundleChapterMap composite PK and FKs
        modelBuilder.Entity<BundleChapterMap>()
            .HasKey(b => new { b.VolumeKey, b.ChapterKey });
        modelBuilder.Entity<BundleChapterMap>()
            .HasOne(b => b.Volume)
            .WithMany()
            .HasForeignKey(b => b.VolumeKey)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<BundleChapterMap>()
            .HasOne(b => b.Chapter)
            .WithMany()
            .HasForeignKey(b => b.ChapterKey)
            .OnDelete(DeleteBehavior.Cascade);
    }

    public async Task<string?> FindMangaLike(Series other, CancellationToken ct)
    {
        if (await Series.FirstOrDefaultAsync(m => m.Key == other.Key, ct) is { } f)
            return other.Key;

        var mangas = await MangaWithMetadata().Select(m => new
        {
            Id = m.Key,
            AltTitles = m.AltTitles.Select(a => a.Title).ToList(),
            Links = m.Links.Select(l => l.LinkUrl).ToList()
        }).ToListAsync(ct);

        if (mangas.FirstOrDefault(m =>
                m.Id == other.Key ||
                m.AltTitles.Any(t => other.AltTitles.Any(ot => ot.Title.Equals(t)) ||
                m.Links.Any(l => other.Links.Any(ol => ol.LinkUrl == l))))
            is { } manga)
            return manga.Id;

        return null;
    }

    public IIncludableQueryable<Series, ICollection<AltTitle>> MangaWithMetadata() =>
        Series
            .Include(m => m.Library)
            .Include(m => m.Authors)
            .Include(m => m.MangaTags)
            .Include(m => m.Links)
            .Include(m => m.AltTitles);

    public IIncludableQueryable<Series, ICollection<SourceId<Series>>> MangaIncludeAll() =>
        MangaWithMetadata()
            .Include(m => m.Chapters)
            .Include(m => m.SourceIds);

    /// <summary>
    /// Upserts a Series into the database: finds an existing match or inserts a new one,
    /// merges tags/authors, and syncs. Does NOT kick off any background workers.
    /// </summary>
    public async Task<(Series manga, SourceId<Series> id)?> UpsertManga(
        Series addManga, SourceId<Series> addMcId, CancellationToken token)
    {
        Log.DebugFormat("Upserting Series: {0}", addManga);
        (Series, SourceId<Series>)? result;

        if (await FindMangaLike(addManga, token) is { } mangaId)
        {
            Series manga = await MangaIncludeAll().FirstAsync(m => m.Key == mangaId, token);
            Log.DebugFormat("Merging with existing Series: {0}", manga);

            var existingMcId = manga.SourceIds
                .FirstOrDefault(id => id.MangaConnectorName == addMcId.MangaConnectorName
                                      && id.IdOnConnectorSite == addMcId.IdOnConnectorSite);

            SourceId<Series> mcIdToUse;
            if (existingMcId == null)
            {
                mcIdToUse = new SourceId<Series>(manga, addMcId.MangaConnectorName, addMcId.IdOnConnectorSite, addMcId.WebsiteUrl, addMcId.UseForDownload);
                manga.SourceIds.Add(mcIdToUse);
            }
            else
            {
                mcIdToUse = existingMcId;
                if (existingMcId.WebsiteUrl != addMcId.WebsiteUrl)
                {
                    var updatedMcId = new SourceId<Series>(manga, existingMcId.MangaConnectorName, existingMcId.IdOnConnectorSite, addMcId.WebsiteUrl, existingMcId.UseForDownload);
                    manga.SourceIds.Remove(existingMcId);
                    manga.SourceIds.Add(updatedMcId);
                    mcIdToUse = updatedMcId;
                }
            }

            result = (manga, mcIdToUse);
        }
        else
        {
            Log.Debug("Series does not exist yet, inserting.");
            IEnumerable<SeriesTag> mergedTags = addManga.MangaTags.Select(mt =>
            {
                SeriesTag? inDb = Tags.Find(mt.Tag);
                return inDb ?? mt;
            });
            addManga.MangaTags = mergedTags.ToList();

            IEnumerable<Author> mergedAuthors = addManga.Authors.Select(ma =>
            {
                Author? inDb = Authors.Find(ma.Key);
                return inDb ?? ma;
            });
            addManga.Authors = mergedAuthors.ToList();

            Series.Add(addManga);
            addManga.SourceIds.Add(addMcId);
            result = (addManga, addMcId);
        }

        if (await Sync(token, reason: "UpsertManga") is { success: false })
            return null;

        return result;
    }

    /// <summary>
    /// Convenience overload that unpacks a tuple.
    /// </summary>
    public Task<(Series manga, SourceId<Series> id)?> AddMangaToContext(
        (Series, SourceId<Series>) addManga, CancellationToken token)
        => UpsertManga(addManga.Item1, addManga.Item2, token);
}
