using AFHSync.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AFHSync.Tests.Integration;

/// <summary>Phase 3 (§3.6): one Exchange Online session per process, not one per request.</summary>
[Trait("Category", "Integration")]
public class DiLifetimeTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public DiLifetimeTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void DdgResolver_IsASingleton()
    {
        using var scope1 = _factory.Services.CreateScope();
        using var scope2 = _factory.Services.CreateScope();

        var a = scope1.ServiceProvider.GetRequiredService<IDDGResolver>();
        var b = scope2.ServiceProvider.GetRequiredService<IDDGResolver>();

        Assert.Same(a, b);
    }
}
