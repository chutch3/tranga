using API.MangaConnectors;
using API.Schema.MangaContext.MetadataFetchers;
using log4net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace API.Schema.MangaContext;

public class MangaContext(DbContextOptions<MangaContext> options) : TrangaBaseContext<MangaContext>(options)
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(MangaContext));
    public DbSet<Manga> Mangas { get; set; }
    public DbSet<FileLibrary> FileLibraries { get; set; }
    public DbSet<Chapter> Chapters { get; set; }
    public DbSet<Author> Authors { get; set; }
    public DbSet<MangaTag> Tags { get; set; }
    public DbSet<MangaConnectorId<Manga>> MangaConnectorToManga { get; set; }
    public DbSet<MangaConnectorId<Chapter>> MangaConnectorToChapter { get; set; }
    public DbSet<MetadataEntry> MetadataEntries { get; set; }

    public IQueryable<Manga> GetTrackedMangas() =>
        Mangas
            .Include(m => m.MangaConnectorIds)
            .Where(m => m.IsTracked
                        || m.MangaConnectorIds.Any(id => id.UseForDownload)
                        || Chapters.Any(c => c.ParentMangaId == m.Key && c.Downloaded));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        //Manga has many Chapters
        modelBuilder.Entity<Manga>()
            .HasMany<Chapter>(m => m.Chapters)
            .WithOne(c => c.ParentManga)
            .HasForeignKey(c => c.ParentMangaId)
            .OnDelete(DeleteBehavior.Cascade);
        //Chapter has MangaConnectorIds
        modelBuilder.Entity<Chapter>()
            .HasMany<MangaConnectorId<Chapter>>(c => c.MangaConnectorIds)
            .WithOne(id => id.Obj)
            .HasForeignKey(id => id.ObjId)
            .OnDelete(DeleteBehavior.Cascade);
        //Manga owns MangaAltTitles
        modelBuilder.Entity<Manga>()
            .OwnsMany<AltTitle>(m => m.AltTitles)
            .WithOwner();
        modelBuilder.Entity<Manga>()
            .Navigation(m => m.AltTitles)
            .AutoInclude();
        //Manga owns Links
        modelBuilder.Entity<Manga>()
            .OwnsMany<Link>(m => m.Links)
            .WithOwner();
        modelBuilder.Entity<Manga>()
            .Navigation(m => m.Links)
            .AutoInclude();
        //Manga has many Tags associated with many Obj
        modelBuilder.Entity<Manga>()
            .HasMany<MangaTag>(m => m.MangaTags)
            .WithMany()
            .UsingEntity("MangaTagToManga",
                l => l.HasOne(typeof(MangaTag)).WithMany().HasForeignKey("MangaTagIds")
                    .HasPrincipalKey(nameof(MangaTag.Tag)),
                r => r.HasOne(typeof(Manga)).WithMany().HasForeignKey("MangaIds").HasPrincipalKey(nameof(Manga.Key)),
                j => j.HasKey("MangaTagIds", "MangaIds")
            );
        modelBuilder.Entity<Manga>()
            .Navigation(m => m.MangaTags)
            .AutoInclude();
        //Manga has many Authors associated with many Obj
        modelBuilder.Entity<Manga>()
            .HasMany<Author>(m => m.Authors)
            .WithMany()
            .UsingEntity("AuthorToManga",
                l => l.HasOne(typeof(Author)).WithMany().HasForeignKey("AuthorIds").HasPrincipalKey(nameof(Author.Key)),
                r => r.HasOne(typeof(Manga)).WithMany().HasForeignKey("MangaIds").HasPrincipalKey(nameof(Manga.Key)),
                j => j.HasKey("AuthorIds", "MangaIds")
            );
        modelBuilder.Entity<Manga>()
            .Navigation(m => m.Authors)
            .AutoInclude();
        //Manga has many MangaIds
        modelBuilder.Entity<Manga>()
            .HasMany<MangaConnectorId<Manga>>(m => m.MangaConnectorIds)
            .WithOne(id => id.Obj)
            .HasForeignKey(id => id.ObjId)
            .OnDelete(DeleteBehavior.Cascade);


        //FileLibrary has many Mangas
        modelBuilder.Entity<FileLibrary>()
            .HasMany<Manga>()
            .WithOne(m => m.Library)
            .HasForeignKey(m => m.LibraryId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<MetadataFetcher>()
            .HasDiscriminator<string>(nameof(MetadataEntry))
            .HasValue<MyAnimeList>(nameof(MyAnimeList));
        //MetadataEntry
        modelBuilder.Entity<MetadataEntry>()
            .HasOne<Manga>(entry => entry.Manga)
            .WithMany()
            .OnDelete(DeleteBehavior.Cascade);
    }

    public async Task<string?> FindMangaLike(Manga other, CancellationToken ct)
    {
        if (await Mangas.FirstOrDefaultAsync(m => m.Key == other.Key, ct) is { } f)
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

    public IIncludableQueryable<Manga, ICollection<AltTitle>> MangaWithMetadata() =>
        Mangas
            .Include(m => m.Library)
            .Include(m => m.Authors)
            .Include(m => m.MangaTags)
            .Include(m => m.Links)
            .Include(m => m.AltTitles);

    public IIncludableQueryable<Manga, ICollection<MangaConnectorId<Manga>>> MangaIncludeAll() =>
        MangaWithMetadata()
            .Include(m => m.Chapters)
            .Include(m => m.MangaConnectorIds);

    /// <summary>
    /// Upserts a Manga into the database: finds an existing match or inserts a new one,
    /// merges tags/authors, and syncs. Does NOT kick off any background workers.
    /// </summary>
    public async Task<(Manga manga, MangaConnectorId<Manga> id)?> UpsertManga(
        Manga addManga, MangaConnectorId<Manga> addMcId, CancellationToken token)
    {
        Log.DebugFormat("Upserting Manga: {0}", addManga);
        (Manga, MangaConnectorId<Manga>)? result;

        if (await FindMangaLike(addManga, token) is { } mangaId)
        {
            Manga manga = await MangaIncludeAll().FirstAsync(m => m.Key == mangaId, token);
            Log.DebugFormat("Merging with existing Manga: {0}", manga);

            var existingMcId = manga.MangaConnectorIds
                .FirstOrDefault(id => id.MangaConnectorName == addMcId.MangaConnectorName
                                      && id.IdOnConnectorSite == addMcId.IdOnConnectorSite);

            MangaConnectorId<Manga> mcIdToUse;
            if (existingMcId == null)
            {
                mcIdToUse = new MangaConnectorId<Manga>(manga, addMcId.MangaConnectorName, addMcId.IdOnConnectorSite, addMcId.WebsiteUrl, addMcId.UseForDownload);
                manga.MangaConnectorIds.Add(mcIdToUse);
            }
            else
            {
                mcIdToUse = existingMcId;
                if (existingMcId.WebsiteUrl != addMcId.WebsiteUrl)
                {
                    var updatedMcId = new MangaConnectorId<Manga>(manga, existingMcId.MangaConnectorName, existingMcId.IdOnConnectorSite, addMcId.WebsiteUrl, existingMcId.UseForDownload);
                    manga.MangaConnectorIds.Remove(existingMcId);
                    manga.MangaConnectorIds.Add(updatedMcId);
                    mcIdToUse = updatedMcId;
                }
            }

            result = (manga, mcIdToUse);
        }
        else
        {
            Log.Debug("Manga does not exist yet, inserting.");
            IEnumerable<MangaTag> mergedTags = addManga.MangaTags.Select(mt =>
            {
                MangaTag? inDb = Tags.Find(mt.Tag);
                return inDb ?? mt;
            });
            addManga.MangaTags = mergedTags.ToList();

            IEnumerable<Author> mergedAuthors = addManga.Authors.Select(ma =>
            {
                Author? inDb = Authors.Find(ma.Key);
                return inDb ?? ma;
            });
            addManga.Authors = mergedAuthors.ToList();

            Mangas.Add(addManga);
            addManga.MangaConnectorIds.Add(addMcId);
            result = (addManga, addMcId);
        }

        if (await Sync(token, reason: "UpsertManga") is { success: false })
            return null;

        return result;
    }

    /// <summary>
    /// Convenience overload that unpacks a tuple.
    /// </summary>
    public Task<(Manga manga, MangaConnectorId<Manga> id)?> AddMangaToContext(
        (Manga, MangaConnectorId<Manga>) addManga, CancellationToken token)
        => UpsertManga(addManga.Item1, addManga.Item2, token);
}
