using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Olga.Core.Tests;

public sealed class OpenApiContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public OpenApiContractTests(WebApplicationFactory<Program> factory) => this.factory = factory;

    [Fact]
    public async Task Generated_document_describes_the_supported_authentication_schemes()
    {
        using var document = await GetDocumentAsync();
        var schemes = document.RootElement.GetProperty("components").GetProperty("securitySchemes");

        var bearer = schemes.GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        Assert.Equal("JWT", bearer.GetProperty("bearerFormat").GetString());

        var localMember = schemes.GetProperty("LocalMember");
        Assert.Equal("apiKey", localMember.GetProperty("type").GetString());
        Assert.Equal("X-Member-Id", localMember.GetProperty("name").GetString());
        Assert.Equal("header", localMember.GetProperty("in").GetString());
    }

    [Fact]
    public async Task Generated_document_marks_only_protected_operations()
    {
        using var document = await GetDocumentAsync();
        var paths = document.RootElement.GetProperty("paths");

        var protectedSecurity = paths
            .GetProperty("/v1/me/profile")
            .GetProperty("get")
            .GetProperty("security")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(2, protectedSecurity.Length);
        Assert.Contains(protectedSecurity, requirement => requirement.TryGetProperty("Bearer", out _));
        Assert.Contains(protectedSecurity, requirement => requirement.TryGetProperty("LocalMember", out _));
        Assert.DoesNotContain(
            protectedSecurity,
            requirement => requirement.TryGetProperty("Bearer", out _)
                && requirement.TryGetProperty("LocalMember", out _));

        Assert.False(paths.GetProperty("/v1/events").GetProperty("get").TryGetProperty("security", out _));
        AssertOperationIsAnonymousWhenDocumented(paths, "/health", "get");
        AssertOperationIsAnonymousWhenDocumented(paths, "/ready", "get");
    }

    [Fact]
    public async Task Generated_document_uses_only_the_browser_https_origin_behind_a_proxy()
    {
        using var document = await GetDocumentAsync(forwardedProto: "https");

        var servers = document.RootElement.GetProperty("servers").EnumerateArray().ToArray();
        var server = Assert.Single(servers);
        var url = server.GetProperty("url").GetString();

        Assert.Equal("/", url);
        Assert.DoesNotContain("http://", url!, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<JsonDocument> GetDocumentAsync(string? forwardedProto = null)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/swagger/v1/swagger.json");
        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
    }

    private static void AssertOperationIsAnonymousWhenDocumented(
        JsonElement paths,
        string path,
        string method)
    {
        if (!paths.TryGetProperty(path, out var pathItem)
            || !pathItem.TryGetProperty(method, out var operation))
        {
            return;
        }

        Assert.False(operation.TryGetProperty("security", out _));
    }
}
