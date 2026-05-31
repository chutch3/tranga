using API.Indexers;
using Moq;
using Xunit;

namespace API.Tests.Indexers;

public class AggregateIndexerSearchTests
{
    private static IIndexer IndexerReturning(string name, params IndexerSearchResult[] results)
    {
        var m = new Mock<IIndexer>();
        m.SetupGet(i => i.Name).Returns(name);
        m.Setup(i => i.Search(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(results);
        return m.Object;
    }

    private static IIndexerProvider ProviderWith(params IIndexer[] indexers)
    {
        var p = new Mock<IIndexerProvider>();
        p.Setup(x => x.GetIndexersAsync(It.IsAny<CancellationToken>()))
         .ReturnsAsync(indexers.ToList());
        return p.Object;
    }

    private static IndexerSearchResult R(string title, string url, int seeders) =>
        new(title, url, 1000, seeders, "ix");

    [Fact]
    public async Task Search_FansOutAcrossAllProvidersAndIndexers()
    {
        var providerA = ProviderWith(IndexerReturning("A", R("Saga 60 [A]", "magnet:a", 10)));
        var providerB = ProviderWith(IndexerReturning("B", R("Saga 60 [B]", "magnet:b", 20)));

        var search = new AggregateIndexerSearch([providerA, providerB]);

        var results = await search.Search(new IndexerQuery("Saga", "60"), CancellationToken.None);

        Assert.Equal(2, results.Length);
        Assert.Contains(results, r => r.DownloadUrl == "magnet:a");
        Assert.Contains(results, r => r.DownloadUrl == "magnet:b");
    }

    [Fact]
    public async Task Search_DeduplicatesByDownloadUrl()
    {
        // Same release surfaced by two indexers (common with Prowlarr): keep one.
        var p1 = ProviderWith(IndexerReturning("A", R("Saga 60", "magnet:dup", 10)));
        var p2 = ProviderWith(IndexerReturning("B", R("Saga 60", "magnet:dup", 99)));

        var search = new AggregateIndexerSearch([p1, p2]);

        var results = await search.Search(new IndexerQuery("Saga", "60"), CancellationToken.None);

        Assert.Single(results);
    }

    [Fact]
    public async Task Search_OneFailingIndexerDoesNotSinkTheOthers()
    {
        var good = IndexerReturning("Good", R("Saga 60", "magnet:good", 10));
        var bad = new Mock<IIndexer>();
        bad.SetupGet(i => i.Name).Returns("Bad");
        bad.Setup(i => i.Search(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
           .ThrowsAsync(new HttpRequestException("boom"));

        var search = new AggregateIndexerSearch([ProviderWith(good, bad.Object)]);

        var results = await search.Search(new IndexerQuery("Saga", "60"), CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("magnet:good", results[0].DownloadUrl);
    }

    [Fact]
    public async Task Search_ReturnsEmpty_WhenNoProvidersConfigured()
    {
        var search = new AggregateIndexerSearch([]);

        Assert.Empty(await search.Search(new IndexerQuery("Saga"), CancellationToken.None));
    }
}
