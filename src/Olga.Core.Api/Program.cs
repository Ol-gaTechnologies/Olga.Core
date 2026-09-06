using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Olga.Core.Application;
using Olga.Core.Contracts;
using Olga.Core.Domain;
using Olga.Core.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower; o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull; });
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
var connection = builder.Configuration.GetConnectionString("AzureSql");
var local = string.IsNullOrWhiteSpace(connection);
if (local) builder.Services.AddDbContext<CoreDbContext>(o => o.UseInMemoryDatabase("olga-core-local"));
else builder.Services.AddDbContext<CoreDbContext>(o => o.UseSqlServer(connection));
builder.Services.AddScoped<ICoreStore>(sp => sp.GetRequiredService<CoreDbContext>());
builder.Services.AddScoped<ICoreService, CoreService>();

var app = builder.Build();
var serviceToken = app.Configuration["ServiceAuthorization:Token"];
if (!local && string.IsNullOrWhiteSpace(serviceToken)) throw new InvalidOperationException("ServiceAuthorization:Token is required when Azure SQL is configured.");
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Correlation-Id"] = context.TraceIdentifier;
    try
    {
        if (context.Request.ContentLength is > 256_000) { await Error(context, 413, "PAYLOAD_TOO_LARGE"); return; }
        await next();
    }
    catch (DomainException ex) { await Error(context, ex.StatusCode, ex.Code); }
    catch (DbUpdateConcurrencyException) { await Error(context, 409, "RESOURCE_VERSION_CONFLICT"); }
    catch (Exception) { await Error(context, 500, "INTERNAL_ERROR"); }
});

app.MapOpenApi();
app.MapHealthChecks("/health");
app.MapGet("/ready", async (CoreDbContext db, CancellationToken ct) => await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));

app.MapGet("/v1/me/profile", async (HttpContext c, ICoreService s, CancellationToken ct) => { var id = Member(c, app); var value = await s.GetOwnProfileAsync(id, ct); c.Response.Headers.ETag = value.ETag; return Results.Ok(value); });
app.MapPatch("/v1/me/profile", async (HttpContext c, ProfileUpdateRequest body, ICoreService s, CancellationToken ct) => { var value = await s.UpdateProfileAsync(Member(c, app), body, c.Request.Headers.IfMatch.FirstOrDefault(), ct); c.Response.Headers.ETag = value.ETag; return Results.Ok(value); });
app.MapGet("/v1/members/{memberId}", async (HttpContext c, string memberId, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetVisibleProfileAsync(Member(c, app), memberId, ct)));
app.MapPost("/v1/me/consents", async (HttpContext c, ConsentRequest body, ICoreService s, CancellationToken ct) => Results.Created("/v1/me/consents", await s.RecordConsentAsync(Member(c, app), body, ct)));
app.MapGet("/v1/events", async (ICoreService s, CancellationToken ct) => Results.Ok(await s.GetEventsAsync(ct)));
app.MapPost("/v1/events/{eventId}/register", async (HttpContext c, string eventId, ICoreService s, CancellationToken ct) => Results.Ok(await s.RegisterAsync(Member(c, app), eventId, ct)));
app.MapPost("/v1/events/{eventId}/live-mode", async (HttpContext c, string eventId, LiveModeRequest body, ICoreService s, CancellationToken ct) => Results.Ok(await s.StartLiveModeAsync(Member(c, app), eventId, body, ct)));
app.MapDelete("/v1/events/{eventId}/live-mode", async (HttpContext c, string eventId, ICoreService s, CancellationToken ct) => { await s.StopLiveModeAsync(Member(c, app), eventId, ct); return Results.NoContent(); });
app.MapPost("/v1/events/{eventId}/presence", async (HttpContext c, string eventId, PresenceRequest body, ICoreService s, CancellationToken ct) => { await s.RecordPresenceAsync(Member(c, app), eventId, body, ct); return Results.Accepted(); });
app.MapPost("/v1/connection-requests", async (HttpContext c, ConnectionRequestCreate body, ICoreService s, CancellationToken ct) => Results.Created("/v1/connection-requests", await s.CreateConnectionRequestAsync(Member(c, app), body, ct)));
app.MapPatch("/v1/connection-requests/{requestId}", async (HttpContext c, string requestId, ConnectionDecisionRequest body, ICoreService s, CancellationToken ct) => Results.Ok(await s.DecideConnectionRequestAsync(Member(c, app), requestId, body, ct)));
app.MapGet("/v1/connections", async (HttpContext c, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetConnectionsAsync(Member(c, app), ct)));
app.MapPost("/v1/members/block", async (HttpContext c, BlockRequest body, ICoreService s, CancellationToken ct) => { await s.BlockAsync(Member(c, app), body, ct); return Results.NoContent(); });
app.MapGet("/v1/conversations/{conversationId}/messages", async (HttpContext c, string conversationId, long? after, int? limit, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetMessagesAsync(Member(c, app), conversationId, after ?? 0, limit ?? 50, ct)));
app.MapPost("/v1/conversations/{conversationId}/messages", async (HttpContext c, string conversationId, MessageCreateRequest body, ICoreService s, CancellationToken ct) => Results.Created($"/v1/conversations/{conversationId}/messages/{body.MessageId}", await s.SendMessageAsync(Member(c, app), conversationId, body, ct)));
app.MapPatch("/v1/me/notification-preferences", async (HttpContext c, NotificationPreferenceRequest body, ICoreService s, CancellationToken ct) => Results.Ok(await s.SetNotificationPreferenceAsync(Member(c, app), body, ct)));
app.MapPost("/v1/me/privacy-requests", async (HttpContext c, PrivacyRequestCreate body, ICoreService s, CancellationToken ct) => Results.Accepted("/v1/me/privacy-requests", await s.CreatePrivacyRequestAsync(Member(c, app), body, ct)));
app.MapGet("/v1/sync/changes", async (HttpContext c, long? after, int? limit, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetChangesAsync(Member(c, app), after ?? 0, limit ?? 100, ct)));

app.MapGet("/v1/internal/nlp/eligibility/{contextId}/{memberId}", async (HttpContext c, string contextId, string memberId, ICoreService s, CancellationToken ct) => { RequireService(c, serviceToken, app.Environment); return Results.Ok(await s.GetNlpEligibilityAsync(memberId, contextId, ct)); });
app.MapGet("/v1/internal/nlp/relationships/{requesterId}/{candidateId}", async (HttpContext c, string requesterId, string candidateId, ICoreService s, CancellationToken ct) => { RequireService(c, serviceToken, app.Environment); return Results.Ok(await s.GetNlpRelationshipAsync(requesterId, candidateId, ct)); });

if (local) await LocalDevelopmentSeeder.SeedAsync(app.Services, CancellationToken.None);
app.Run();

static string Member(HttpContext context, WebApplication app)
{
    var id = context.User.FindFirst("sub")?.Value;
    if (string.IsNullOrWhiteSpace(id) && app.Environment.IsDevelopment() && app.Configuration.GetValue("Identity:AllowLocalMemberHeader", true)) id = context.Request.Headers["X-Member-Id"].FirstOrDefault();
    return !string.IsNullOrWhiteSpace(id) ? id : throw new DomainException("MEMBER_IDENTITY_REQUIRED", 401);
}

static void RequireService(HttpContext context, string? expected, IWebHostEnvironment environment)
{
    if (environment.IsDevelopment() && string.IsNullOrWhiteSpace(expected)) return;
    var supplied = context.Request.Headers["X-Service-Token"].ToString();
    if (string.IsNullOrWhiteSpace(expected) || !FixedEquals(supplied, expected)) throw new DomainException("SERVICE_IDENTITY_REQUIRED", 401);
}

static bool FixedEquals(string supplied, string expected)
{
    var left = Encoding.UTF8.GetBytes(supplied); var right = Encoding.UTF8.GetBytes(expected);
    return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
}

static async Task Error(HttpContext context, int status, string code)
{
    if (context.Response.HasStarted) return;
    context.Response.StatusCode = status; context.Response.ContentType = "application/problem+json";
    await context.Response.WriteAsJsonAsync(new ApiError(code, code switch { "MEMBER_IDENTITY_REQUIRED" => "An authenticated member identity is required.", "IF_MATCH_REQUIRED" => "An If-Match header is required.", "RESOURCE_VERSION_CONFLICT" => "The resource changed since it was read.", "INTERNAL_ERROR" => "The request could not be completed.", _ => "The request is invalid or cannot be completed in its current state." }, context.TraceIdentifier));
}

public partial class Program { }
