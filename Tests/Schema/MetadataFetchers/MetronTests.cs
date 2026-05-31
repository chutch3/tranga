using API;
using API.Schema.SeriesContext;
using API.Schema.SeriesContext.MetadataFetchers;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace API.Tests.Schema.MetadataFetchers;

public class MetronTests
{
    [Fact]
    public void Name_IsMetron()
    {
        var metron = new Metron(Mock.Of<IMetronClient>());
        Assert.Equal("Metron", metron.Name);
    }

    [Fact]
    public async Task SearchMetadataEntry_MapsClientResultsToSearchResults()
    {
        var client = new Mock<IMetronClient>();
        client.Setup(c => c.SearchSeries("Saga", It.IsAny<CancellationToken>()))
              .ReturnsAsync([new MetronSeries("2419", "Saga", "https://metron.cloud/series/2419/", "desc", 2012, "http://cover")]);
        var metron = new Metron(client.Object);

        var results = await metron.SearchMetadataEntry("Saga");

        var r = Assert.Single(results);
        Assert.Equal("2419", r.Identifier);
        Assert.Equal("Saga", r.Name);
        Assert.Equal("http://cover", r.CoverUrl);
    }

    [Fact]
    public async Task UpdateMetadata_AppliesNameDescriptionAndCoverToSeries()
    {
        var options = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase("metron-update-" + Guid.NewGuid().ToString("N")).Options;
        await using var context = new SeriesContext(options);

        var library = new FileLibrary("/tmp", "Lib");
        context.FileLibraries.Add(library);
        // Sparse series, as produced by the indexer-backed (torrent) source.
        var series = new Series("Saga", "", "", SeriesReleaseStatus.Continuing,
            new List<Author>(), new List<SeriesTag>(), new List<Link>(), new List<AltTitle>(),
            library, 0f, null, "en");
        context.Series.Add(series);
        await context.SaveChangesAsync();

        var client = new Mock<IMetronClient>();
        client.Setup(c => c.GetSeries("2419", It.IsAny<CancellationToken>()))
              .ReturnsAsync(new MetronSeries("2419", "Saga", "https://metron.cloud/series/2419/",
                  "A sweeping space opera.", 2012, "https://static.metron.cloud/saga.jpg"));
        var metron = new Metron(client.Object);

        var entry = new MetadataEntry(metron, series, "2419");

        await metron.UpdateMetadata(entry, context, CancellationToken.None);

        // `series` is the tracked instance UpdateMetadata mutated in place.
        Assert.Equal("A sweeping space opera.", series.Description);
        Assert.Equal("https://static.metron.cloud/saga.jpg", series.CoverUrl);
    }

    [Fact]
    public async Task UpdateMetadata_DoesNothing_WhenMetronHasNoDetail()
    {
        var options = new DbContextOptionsBuilder<SeriesContext>()
            .UseInMemoryDatabase("metron-nodetail-" + Guid.NewGuid().ToString("N")).Options;
        await using var context = new SeriesContext(options);
        var library = new FileLibrary("/tmp", "Lib");
        context.FileLibraries.Add(library);
        var series = new Series("Saga", "orig", "origcover", SeriesReleaseStatus.Continuing,
            new List<Author>(), new List<SeriesTag>(), new List<Link>(), new List<AltTitle>(),
            library, 0f, null, "en");
        context.Series.Add(series);
        await context.SaveChangesAsync();

        var client = new Mock<IMetronClient>();
        client.Setup(c => c.GetSeries(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((MetronSeries?)null);
        var metron = new Metron(client.Object);
        var entry = new MetadataEntry(metron, series, "9999");

        await metron.UpdateMetadata(entry, context, CancellationToken.None);

        Assert.Equal("orig", series.Description); // unchanged
    }
}
