using AFHSync.Api.Services;

namespace AFHSync.Tests.Unit.Api;

public class GraphQueryTests
{
    [Theory]
    [InlineData("O'Brien", "O''Brien")]
    [InlineData("plain", "plain")]
    [InlineData("it''s", "it''''s")]
    [InlineData("", "")]
    public void EscapeLiteral_DoublesSingleQuotes(string input, string expected)
        => Assert.Equal(expected, GraphQuery.EscapeLiteral(input));
}
