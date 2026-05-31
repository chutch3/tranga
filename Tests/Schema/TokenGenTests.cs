using API;
using API.Schema.SeriesContext;
using Xunit;

namespace API.Tests.Schema;

public class TokenGenTests
{
    [Fact]
    public void CreateToken_WithGenericType_ShouldIncludeBacktick()
    {
        // We decided to keep the backtick to avoid breaking existing database records
        var token = TokenGen.CreateToken(typeof(SourceId<Series>), "MangaDex", "12345");
        Assert.Contains("`1", token);
        Assert.StartsWith("SourceId`1-", token);
    }

    [Fact]
    public void CreateToken_WithRegularType_ShouldMatchClassName()
    {
        var token = TokenGen.CreateToken(typeof(Series), "One Piece");
        Assert.StartsWith("Series-", token);
    }
}
