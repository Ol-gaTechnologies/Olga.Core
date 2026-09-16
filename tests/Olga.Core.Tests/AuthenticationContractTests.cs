using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Olga.Core.Tests;

public sealed class AuthenticationContractTests
{
    [Theory]
    [InlineData("/v1/events")]
    [InlineData("/health")]
    [InlineData("/ready")]
    [InlineData("/swagger/v1/swagger.json")]
    [InlineData("/swagger/index.html")]
    public async Task Anonymous_endpoints_remain_callable_without_credentials(string path)
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoint_rejects_missing_and_invalid_credentials()
    {
        await using var factory = new BearerWebApplicationFactory();
        using var client = factory.CreateClient();

        using var missing = await client.GetAsync("/v1/me/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");
        using var invalid = await client.GetAsync("/v1/me/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
    }

    [Fact]
    public async Task Valid_bearer_credential_reaches_the_endpoint()
    {
        await using var factory = new BearerWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.CreateToken("A123"));

        using var response = await client.GetAsync("/v1/me/profile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Development_local_member_header_uses_a_raw_member_id()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Member-Id", "A123");

        using var response = await client.GetAsync("/v1/me/profile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class BearerWebApplicationFactory : WebApplicationFactory<Program>
    {
        private const string Issuer = "https://issuer.example.test";
        private const string Audience = "olga-core-tests";
        private readonly byte[] signingKey = RandomNumberGenerator.GetBytes(32);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options =>
                    {
                        options.Authority = null;
                        options.MetadataAddress = null;
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true,
                            ValidIssuer = Issuer,
                            ValidateAudience = true,
                            ValidAudience = Audience,
                            ValidateIssuerSigningKey = true,
                            IssuerSigningKey = new SymmetricSecurityKey(signingKey),
                            ValidateLifetime = true,
                            ClockSkew = TimeSpan.Zero
                        };
                    });
            });
        }

        public string CreateToken(string subject)
        {
            var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                alg = "HS256",
                typ = "JWT"
            }));
            var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = Issuer,
                aud = Audience,
                sub = subject,
                exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()
            }));
            var unsignedToken = $"{header}.{payload}";
            using var hmac = new HMACSHA256(signingKey);
            var signature = Base64UrlEncode(hmac.ComputeHash(Encoding.ASCII.GetBytes(unsignedToken)));
            return $"{unsignedToken}.{signature}";
        }

        private static string Base64UrlEncode(byte[] value) =>
            Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
