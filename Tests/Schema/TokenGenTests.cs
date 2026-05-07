using API;
using API.Schema.MangaContext;
using Xunit;

namespace API.Tests.Schema;

public class TokenGenTests
{
    [Fact]
    public void CreateToken_WithGenericType_ShouldIncludeBacktick()
    {
        // We decided to keep the backtick to avoid breaking existing database records
        var token = TokenGen.CreateToken(typeof(MangaConnectorId<Manga>), "MangaDex", "12345");
        Assert.Contains("`1", token);
        Assert.StartsWith("MangaConnectorId`1-", token);
    }

    [Fact]
    public void CreateToken_WithRegularType_ShouldMatchClassName()
    {
        var token = TokenGen.CreateToken(typeof(Manga), "One Piece");
        Assert.StartsWith("Manga-", token);
    }
}
