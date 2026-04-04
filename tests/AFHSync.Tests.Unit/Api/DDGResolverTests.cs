using AFHSync.Api.Services;

namespace AFHSync.Tests.Unit.Api;

/// <summary>
/// DDGResolver tests. The actual PowerShell runspace requires Exchange Online
/// connectivity, so meaningful tests are integration-level only.
/// These tests validate the type structure and interface compliance.
/// </summary>
public class DDGResolverTests
{
    [Fact]
    public void DDGResolver_ImplementsIDDGResolver()
    {
        Assert.True(typeof(IDDGResolver).IsAssignableFrom(typeof(DDGResolver)));
    }

    [Fact]
    public void DDGResolver_ImplementsIDisposable()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(DDGResolver)));
    }

    [Fact]
    public void DdgInfo_Record_HasRequiredProperties()
    {
        var info = new DdgInfo(
            Id: "abc-123",
            DisplayName: "Buckhead",
            PrimarySmtpAddress: "buckhead@atlantafinehomes.com",
            RecipientFilter: "(Office -eq 'Buckhead')");

        Assert.Equal("abc-123", info.Id);
        Assert.Equal("Buckhead", info.DisplayName);
        Assert.Equal("buckhead@atlantafinehomes.com", info.PrimarySmtpAddress);
        Assert.Equal("(Office -eq 'Buckhead')", info.RecipientFilter);
    }

    [Fact(Skip = "Requires live Exchange Online connection")]
    public async Task ListDdgsAsync_ReturnsGroupsFromExchange()
    {
        // Integration test: requires Exchange Online with ExchangeOnlineManagement module
        // and certificate-based auth configured
        await Task.CompletedTask;
    }
}
