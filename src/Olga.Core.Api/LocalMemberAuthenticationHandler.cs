using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Olga.Core.Api;

internal static class CoreAuthenticationSchemes
{
    public const string Bearer = "Bearer";
    public const string LocalMember = "LocalMember";
    public const string LocalMemberHeader = "X-Member-Id";
    public const string MemberSelector = "MemberSelector";
}

internal sealed class LocalMemberAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var memberId = Request.Headers[CoreAuthenticationSchemes.LocalMemberHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(memberId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [new Claim("sub", memberId)],
            CoreAuthenticationSchemes.LocalMember);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, CoreAuthenticationSchemes.LocalMember);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
