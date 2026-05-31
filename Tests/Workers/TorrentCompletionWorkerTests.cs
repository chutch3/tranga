using System.IO.Compression;
using API;
using API.Acquirers;
using API.MangaConnectors;
using API.Schema.SeriesContext;
using API.TorrentClients;
using API.Workers.PeriodicWorkers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace API.Tests.Workers;

public class TorrentCompletionWorkerTests
{
    private sealed class FakeTorrentSource : SeriesSource
    {
        public FakeTorrentSource(TrangaSettings s) : base("FakeTorrent", ["en"], ["fake.test"], "i", s) { }
        public override AcquisitionKind Kind => AcquisitionKind.Torrent;
        public override Task<(Series, SourceId<Series>)[]> SearchManga(string s) => throw new NotSupportedException();
        public override Task<(Series, SourceId<Series>)?> GetMangaFromUrl(string u) => throw new NotSupportedException();
        public override Task<(Series, SourceId<Series>)?> GetMangaFromId(string i) => throw new NotSupportedException();
        public override Task<(Chapter, SourceId<Chapter>)[]> GetChapters(SourceId<Series> m, string? l = null) => throw new NotSupportedException();
        internal override Task<string[]> GetChapterImageUrls(SourceId<Chapter> c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task DoWork_MovesCbzAndMarksDownloaded_WhenTorrentCompleted()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tranga-cw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string libraryRoot = Path.Combine(tempRoot, "library");
        Directory.CreateDirectory(libraryRoot);
        string savePath = Path.Combine(tempRoot, "torrent-out");
        Directory.CreateDirectory(savePath);
        // Plant a fake .cbz inside the staging dir to simulate a completed torrent
        string sourceCbz = Path.Combine(savePath, "saga-60.cbz");
        using (var zip = ZipFile.Open(sourceCbz, ZipArchiveMode.Create)) { zip.CreateEntry("0.jpg"); }
        try
        {
            var options = new DbContextOptionsBuilder<SeriesContext>()
                .UseInMemoryDatabase(databaseName: "torrent-completion-" + Guid.NewGuid().ToString("N"))
                .Options;
            using var context = new SeriesContext(options);
            var library = new FileLibrary(libraryRoot, "Lib");
            context.FileLibraries.Add(library);
            var series = new Series("Saga", "d", "c", SeriesReleaseStatus.Continuing,
                new List<Author>(), new List<SeriesTag>(), new List<Link>(), new List<AltTitle>(),
                library, 0f, 2024, "en");
            context.Series.Add(series);
            var chapter = new Chapter(series, "60", null, null);
            context.Chapters.Add(chapter);
            var chId = new SourceId<Chapter>(chapter, "FakeTorrent", "60", "magnet:?xt=urn:btih:abc", true);
            context.MangaConnectorToChapter.Add(chId);
            await context.SaveChangesAsync();

            var settings = new TrangaSettings { AppData = tempRoot, ChapterNamingScheme = "%M - Ch.%C" };
            var torrent = new Mock<ITorrentClient>();
            torrent.Setup(t => t.GetStatus(chId.Key, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new TorrentStatus.Completed(savePath));

            bool removeCalled = false;
            torrent.Setup(t => t.Remove(chId.Key, false, It.IsAny<CancellationToken>()))
                   .Callback(() => removeCalled = true)
                   .Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddSingleton(context);
            services.AddDbContext<API.Schema.ActionsContext.ActionsContext>(o => o.UseInMemoryDatabase("Actions-" + Guid.NewGuid().ToString("N")));
            var sp = services.BuildServiceProvider();

            var worker = new TorrentCompletionWorker(torrent.Object, [new FakeTorrentSource(settings)], settings);

            await worker.DoWork(sp.CreateScope());

            // Chapter is now marked downloaded
            var updated = await context.Chapters.FirstAsync(c => c.Key == chapter.Key);
            Assert.True(updated.Downloaded, "Chapter should be marked downloaded after torrent finalisation.");
            Assert.False(string.IsNullOrEmpty(updated.FileName));

            // The .cbz lives at the publication path
            string expected = chapter.GetFullFilepath(settings.ChapterNamingScheme)!;
            Assert.True(File.Exists(expected), $"Expected .cbz at {expected}.");

            // The original staging file moved (not copied)
            Assert.False(File.Exists(sourceCbz), "Source .cbz should have been moved, not copied.");

            // Torrent client was told to remove the torrent (keep seeded data)
            Assert.True(removeCalled, "ITorrentClient.Remove must be called after finalisation.");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task DoWork_DoesNothing_WhenTorrentNotYetCompleted()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "tranga-cw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var options = new DbContextOptionsBuilder<SeriesContext>()
                .UseInMemoryDatabase(databaseName: "torrent-pending-" + Guid.NewGuid().ToString("N"))
                .Options;
            using var context = new SeriesContext(options);
            var library = new FileLibrary(Path.Combine(tempRoot, "lib"), "Lib");
            context.FileLibraries.Add(library);
            var series = new Series("Saga", "d", "c", SeriesReleaseStatus.Continuing,
                new List<Author>(), new List<SeriesTag>(), new List<Link>(), new List<AltTitle>(),
                library, 0f, 2024, "en");
            context.Series.Add(series);
            var chapter = new Chapter(series, "60", null, null);
            context.Chapters.Add(chapter);
            var chId = new SourceId<Chapter>(chapter, "FakeTorrent", "60", "magnet:?xt=urn:btih:abc", true);
            context.MangaConnectorToChapter.Add(chId);
            await context.SaveChangesAsync();

            var settings = new TrangaSettings { AppData = tempRoot };
            var torrent = new Mock<ITorrentClient>();
            torrent.Setup(t => t.GetStatus(chId.Key, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new TorrentStatus.Downloading(0.3));

            var services = new ServiceCollection();
            services.AddSingleton(context);
            services.AddDbContext<API.Schema.ActionsContext.ActionsContext>(o => o.UseInMemoryDatabase("Actions-" + Guid.NewGuid().ToString("N")));
            var sp = services.BuildServiceProvider();

            var worker = new TorrentCompletionWorker(torrent.Object, [new FakeTorrentSource(settings)], settings);

            await worker.DoWork(sp.CreateScope());

            var updated = await context.Chapters.FirstAsync(c => c.Key == chapter.Key);
            Assert.False(updated.Downloaded, "Chapter must not be marked downloaded while torrent is still downloading.");
            torrent.Verify(t => t.Remove(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { try { Directory.Delete(tempRoot, recursive: true); } catch { } }
    }
}
