using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Olga.Core.Tests;

public sealed class AuthenticationContractTests
{
    [Theory]
    [InlineData("/v1/events")]
    [InlineData("/health")]
    [InlineData("/ready")]
    [InlineData("/swagger/v1/swagger.json")]
    [InlineData("/swagger/index.html")]
    public async Task Endpoints_are_callable_without_credentials(string path)
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Member_endpoint_uses_the_default_member_without_credentials()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/me/profile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Member_header_can_select_a_member_without_authentication()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Member-Id", "A123");

        using var response = await client.GetAsync("/v1/me/profile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
