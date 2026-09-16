using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Olga.Core.Api;

namespace Olga.Core.Tests;

public sealed class HttpsConfigurationTests
{
    [Fact]
    public async Task Azure_ingress_hsts_middleware_adds_the_production_security_header()
    {
        var nextCalled = false;
        var middleware = new AzureIngressHstsMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal("max-age=31536000", context.Response.Headers[HeaderNames.StrictTransportSecurity]);
    }
}
