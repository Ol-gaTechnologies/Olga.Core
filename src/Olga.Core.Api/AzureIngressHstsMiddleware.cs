using Microsoft.Net.Http.Headers;

namespace Olga.Core.Api;

public sealed class AzureIngressHstsMiddleware(RequestDelegate next)
{
    internal const string HeaderValue = "max-age=31536000";

    public async Task InvokeAsync(HttpContext context)
    {
        // Container Apps terminates TLS before forwarding HTTP to Kestrel, so
        // Request.IsHttps is false even though the public production request is HTTPS.
        context.Response.Headers[HeaderNames.StrictTransportSecurity] = HeaderValue;
        await next(context);
    }
}
