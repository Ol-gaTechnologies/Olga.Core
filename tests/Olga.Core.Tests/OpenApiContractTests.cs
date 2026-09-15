using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Olga.Core.Tests;

public sealed class OpenApiContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public OpenApiContractTests(WebApplicationFactory<Program> factory) => this.factory = factory;

    [Fact]
    public async Task Generated_document_uses_only_the_current_origin_as_its_server()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        var servers = document.RootElement.GetProperty("servers").EnumerateArray().ToArray();
        var server = Assert.Single(servers);
        var url = server.GetProperty("url").GetString();

        Assert.Equal("/", url);
        Assert.DoesNotContain("http://", url!, StringComparison.OrdinalIgnoreCase);
    }
}
