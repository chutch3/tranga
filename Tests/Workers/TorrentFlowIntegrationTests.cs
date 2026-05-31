using System.IO.Compression;
using API;
using API.Acquirers;
using API.Indexers;
using API.MangaConnectors;
using API.Schema.SeriesContext;
using API.TorrentClients;
using API.Workers.PeriodicWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace API.Tests.Workers;

/// <summary>
/// Outside-In integration test that exercises the full torrent path: the acquirer hands off to the
/// torrent client; later, the completion worker picks up the finished torrent and finalises the
/// chapter. Only the indexer + torrent client are mocked (the system's external boundaries).
/// </summary>
public class TorrentFlowIntegrationTests
{
    private sealed class FakeTorrentSource : SeriesSource
    {
        public FakeTorrentSource(TrangaSettings s) : base("FakeProwlarr", ["en"], ["fake.test"], "i", s) { }
        public override AcquisitionKind Kind => AcquisitionKind.Torrent;
        public override Task<(Series, SourceId<Series>)[]> SearchManga(string s) => throw new NotSupportedException();
        public override Task<(Series, SourceId<Series>)?> GetMangaFromUrl(string u) => throw new NotSupportedException();
        public override Task<(Series, SourceId<Series>)?> GetMangaFromId(string i) => throw new NotSupportedException();
        public override Task<(Chapter, SourceId<Chapter>)[]> GetChapters(SourceId<Series> m, string? l = null) => throw new NotSupportedException();
        internal override Task<string[]> GetChapterImageUrls(SourceId<Chapter> c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task EndToEnd_AcquirerHandsOff_ThenCompletionWorkerFinalisesChapter()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tranga-flow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string libraryRoot = Path.Combine(tempRoot, "library");
        Directory.CreateDirectory(libraryRoot);
        string stagingRoot = Path.Combine(tempRoot, "staging");
        try
        {
            // --- DB fixture ---
            var options = new DbContextOptionsBuilder<SeriesContext>()
                .UseInMemoryDatabase("flow-" + Guid.NewGuid().ToString("N")).Options;
            using var context = new SeriesContext(options);
            var library = new FileLibrary(libraryRoot, "Lib");
            context.FileLibraries.Add(library);
            var series = new Series("Saga", "d", "c", SeriesReleaseStatus.Continuing,
                new List<Author>(), new List<SeriesTag>(), new List<Link>(), new List<AltTitle>(),
                library, 0f, 2024, "en");
            context.Series.Add(series);
            var chapter = new Chapter(series, "60", null, null);
            context.Chapters.Add(chapter);
            var chId = new SourceId<Chapter>(chapter, "FakeProwlarr", "60", null, true);
            context.MangaConnectorToChapter.Add(chId);
            await context.SaveChangesAsync();

            var settings = new TrangaSettings { AppData = tempRoot, ChapterNamingScheme = "%M - Ch.%C" };
            var source = new FakeTorrentSource(settings);

            // --- Indexer + torrent client mocks ---
            var release = new IndexerSearchResult("Saga 060.cbz", "magnet:?xt=urn:btih:abc", 1024, 50, "ix");
            var indexer = new Mock<IIndexerClient>();
            indexer.Setup(i => i.Search(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync([release]);

            string capturedSaveDir = "";
            var torrent = new Mock<ITorrentClient>();
            torrent.Setup(t => t.Add(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .Callback<string, string, string, CancellationToken>((_, dir, _, _) => capturedSaveDir = dir)
                   .ReturnsAsync(chId.Key);
            // initially still downloading
            torrent.SetupSequence(t => t.GetStatus(chId.Key, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new TorrentStatus.Downloading(0.5))
                   .ReturnsAsync(() =>
                   {
                       // Simulate qBittorrent finishing — plant a .cbz inside the staging dir.
                       string cbz = Path.Combine(capturedSaveDir, "saga-60.cbz");
                       using var z = ZipFile.Open(cbz, ZipArchiveMode.Create);
                       z.CreateEntry("0.jpg");
                       return new TorrentStatus.Completed(capturedSaveDir);
                   });
            torrent.Setup(t => t.Remove(chId.Key, false, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            // --- Acquirer: hands off ---
            var acquirer = new TorrentAcquirer(indexer.Object, torrent.Object,
                new ReleaseSelector { MinSeeders = 1, PreferredTokens = ["cbz"], BlockedTokens = [] },
                new TorrentAcquirerSettings(stagingRoot, [8000]));

            string? earlyResult = await acquirer.AcquireAsync(chId, source, "/unused.cbz", CancellationToken.None);

            Assert.Null(earlyResult); // hand-off semantics
            torrent.Verify(t => t.Add(release.DownloadUrl, It.IsAny<string>(), chId.Key, It.IsAny<CancellationToken>()), Times.Once);

            // --- Tick 1: completion worker — torrent still downloading, nothing happens ---
            var services = new ServiceCollection();
            services.AddSingleton(context);
            services.AddDbContext<API.Schema.ActionsContext.ActionsContext>(o => o.UseInMemoryDatabase("Actions-" + Guid.NewGuid().ToString("N")));
            var sp = services.BuildServiceProvider();

            var completion = new TorrentCompletionWorker(torrent.Object, [source], settings);
            await completion.DoWork(sp.CreateScope());

            var midRun = await context.Chapters.FirstAsync(c => c.Key == chapter.Key);
            Assert.False(midRun.Downloaded, "Chapter must not be downloaded while torrent is still in flight.");

            // --- Tick 2: torrent now reports Completed; worker finalises ---
            await completion.DoWork(sp.CreateScope());

            var final = await context.Chapters.FirstAsync(c => c.Key == chapter.Key);
            Assert.True(final.Downloaded, "Chapter should be marked downloaded after torrent completion.");
            Assert.True(File.Exists(chapter.GetFullFilepath(settings.ChapterNamingScheme)!),
                "Chapter .cbz should be at the publication path.");
            torrent.Verify(t => t.Remove(chId.Key, false, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}
