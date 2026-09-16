using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Npgsql;
using Olga.Core.Api;
using Olga.Core.Application;
using Olga.Core.Contracts;
using Olga.Core.Domain;
using Olga.Core.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower; o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull; });
var allowLocalMemberHeader = builder.Environment.IsDevelopment()
    && builder.Configuration.GetValue("Identity:AllowLocalMemberHeader", false);
var authority = builder.Configuration["Identity:Authority"];
var audience = builder.Configuration["Identity:Audience"];
if (!builder.Environment.IsDevelopment()
    && (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(audience)))
{
    throw new InvalidOperationException(
        "Identity__Authority and Identity__Audience are required outside Development.");
}
if (!string.IsNullOrWhiteSpace(authority)
    && (!Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri)
        || authorityUri.Scheme != Uri.UriSchemeHttps))
{
    throw new InvalidOperationException("Identity__Authority must be an absolute HTTPS URI.");
}

var authentication = builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = allowLocalMemberHeader
        ? CoreAuthenticationSchemes.MemberSelector
        : JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
});
authentication.AddJwtBearer(options =>
{
    options.Authority = authority;
    options.Audience = audience;
    options.MapInboundClaims = false;
    options.RequireHttpsMetadata = true;
});
if (allowLocalMemberHeader)
{
    authentication
        .AddPolicyScheme(CoreAuthenticationSchemes.MemberSelector, null, options =>
        {
            options.ForwardDefaultSelector = context =>
                context.Request.Headers.ContainsKey(CoreAuthenticationSchemes.LocalMemberHeader)
                    ? CoreAuthenticationSchemes.LocalMember
                    : JwtBearerDefaults.AuthenticationScheme;
        })
        .AddScheme<AuthenticationSchemeOptions, LocalMemberAuthenticationHandler>(
            CoreAuthenticationSchemes.LocalMember,
            null);
}
builder.Services.AddAuthorization();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        // Resolve API calls against the origin that served Swagger, never Kestrel's internal HTTP endpoint.
        document.Servers = [new OpenApiServer { Url = "/" }];
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[CoreAuthenticationSchemes.Bearer] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Name = "Authorization",
            In = ParameterLocation.Header,
            Description = "Enter the JWT only. Swagger sends it as: Authorization: Bearer {token}."
        };
        if (allowLocalMemberHeader)
        {
            document.Components.SecuritySchemes[CoreAuthenticationSchemes.LocalMember] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                Name = CoreAuthenticationSchemes.LocalMemberHeader,
                In = ParameterLocation.Header,
                Description = "Development only. Enter a raw seeded member ID without a prefix."
            };
        }
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        var isProtected = metadata.OfType<IAuthorizeData>().Any()
            && !metadata.OfType<IAllowAnonymous>().Any();
        if (!isProtected)
        {
            return Task.CompletedTask;
        }

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(CoreAuthenticationSchemes.Bearer, context.Document)] = []
        });
        if (allowLocalMemberHeader)
        {
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(CoreAuthenticationSchemes.LocalMember, context.Document)] = []
            });
        }
        return Task.CompletedTask;
    });
});
builder.Services.AddHealthChecks();
var connection = builder.Configuration.GetConnectionString("PostgreSql");
var local = string.IsNullOrWhiteSpace(connection);
if (local) builder.Services.AddDbContextPool<CoreDbContext>(o => o.UseInMemoryDatabase("olga-core-local"));
else builder.Services.AddDbContextPool<CoreDbContext>(o => o.UseOlgaPostgreSql(connection!));
builder.Services.AddScoped<ICoreStore>(sp => sp.GetRequiredService<CoreDbContext>());
builder.Services.AddScoped<ICoreService, CoreService>();

var app = builder.Build();
if (app.Environment.IsProduction()) app.UseMiddleware<AzureIngressHstsMiddleware>();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Correlation-Id"] = context.TraceIdentifier;
    try
    {
        if (context.Request.ContentLength is > 256_000) { await Error(context, 413, "PAYLOAD_TOO_LARGE"); return; }
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        if (context.Request.Path.StartsWithSegments("/v1") && context.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128))
        {
            await Error(context, 400, "IDEMPOTENCY_KEY_REQUIRED");
            return;
        }
        await next();
    }
    catch (DomainException ex) { await Error(context, ex.StatusCode, ex.Code); }
    catch (DbUpdateConcurrencyException) { await Error(context, 409, "RESOURCE_VERSION_CONFLICT"); }
    catch (DbUpdateException ex) when (PostgreSqlConfiguration.IsUniqueViolation(ex)) { await Error(context, 409, "RESOURCE_CONFLICT"); }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { await Error(context, 409, "IDEMPOTENCY_KEY_REUSED"); }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation) { await Error(context, 409, "RESOURCE_STATE_CONFLICT"); }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege) { await Error(context, 403, "RESOURCE_FORBIDDEN"); }
    catch (PostgresException ex) when (ex.SqlState == "P0002") { await Error(context, 404, "RESOURCE_NOT_FOUND"); }
    catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidParameterValue) { await Error(context, 400, "REQUEST_INVALID"); }
    catch (Exception ex) when (PostgreSqlConfiguration.IsUnavailable(ex)) { await Error(context, 503, "DATABASE_UNAVAILABLE"); }
    catch (Exception) { await Error(context, 500, "INTERNAL_ERROR"); }
});
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi("/swagger/{documentName}/swagger.json");
app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "swagger";
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "OLGA Core API v1");
});
app.MapHealthChecks("/health");
app.MapGet("/ready", async (CoreDbContext db, CancellationToken ct) => await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));

var v1 = app.MapGroup("/v1").RequireAuthorization();
v1.MapGet("/me/profile", async (HttpContext c, ICoreService s, CancellationToken ct) => { var id = Member(c); var value = await s.GetOwnProfileAsync(id, ct); c.Response.Headers.ETag = value.ETag; return Results.Ok(value); });
v1.MapPatch("/me/profile", async (HttpContext c, ProfileUpdateRequest body, ICoreService s, CancellationToken ct) => { var value = await s.UpdateProfileAsync(Member(c), body, c.Request.Headers.IfMatch.FirstOrDefault(), ct); c.Response.Headers.ETag = value.ETag; return Results.Ok(value); });
v1.MapGet("/members/{memberId}", async (HttpContext c, string memberId, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetVisibleProfileAsync(Member(c), memberId, ct)));
v1.MapPost("/me/consents", async (HttpContext c, ConsentRequest body, ICoreService s, CancellationToken ct) => Results.Created("/v1/me/consents", await s.RecordConsentAsync(Member(c), body, ct)));
v1.MapGet("/events", async (ICoreService s, CancellationToken ct) => Results.Ok(await s.GetEventsAsync(ct))).AllowAnonymous();
v1.MapPost("/events/{eventId}/register", async (HttpContext c, string eventId, ICoreService s, CancellationToken ct) => Results.Ok(await s.RegisterAsync(Member(c), eventId, ct)));
v1.MapPost("/events/{eventId}/live-mode", async (HttpContext c, string eventId, LiveModeRequest body, ICoreService s, CancellationToken ct) => Results.Ok(await s.StartLiveModeAsync(Member(c), eventId, body, ct)));
v1.MapDelete("/events/{eventId}/live-mode", async (HttpContext c, string eventId, ICoreService s, CancellationToken ct) => { await s.StopLiveModeAsync(Member(c), eventId, ct); return Results.NoContent(); });
v1.MapPost("/events/{eventId}/presence", async (HttpContext c, string eventId, PresenceRequest body, ICoreService s, CancellationToken ct) => { await s.RecordPresenceAsync(Member(c), eventId, body, ct); return Results.Accepted(); });
v1.MapPost("/connection-requests", async (HttpContext c, ConnectionRequestCreate body, ICoreService s, CancellationToken ct) => Results.Created("/v1/connection-requests", await s.CreateConnectionRequestAsync(Member(c), body, ct)));
v1.MapPatch("/connection-requests/{requestId}", async (HttpContext c, string requestId, ConnectionDecisionRequest body, ICoreService s, CancellationToken ct) => Results.Ok(await s.DecideConnectionRequestAsync(Member(c), requestId, body, Idempotency(c), ct)));
v1.MapGet("/connections", async (HttpContext c, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetConnectionsAsync(Member(c), ct)));
v1.MapPost("/members/block", async (HttpContext c, BlockRequest body, ICoreService s, CancellationToken ct) => { await s.BlockAsync(Member(c), body, ct); return Results.NoContent(); });
v1.MapGet("/conversations/{conversationId}/messages", async (HttpContext c, string conversationId, long? after, int? limit, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetMessagesAsync(Member(c), conversationId, after ?? 0, limit ?? 50, ct)));
v1.MapPost("/conversations/{conversationId}/messages", async (HttpContext c, string conversationId, MessageCreateRequest body, ICoreService s, CancellationToken ct) => Results.Created($"/v1/conversations/{conversationId}/messages/{body.MessageId}", await s.SendMessageAsync(Member(c), conversationId, body, Idempotency(c), ct)));
v1.MapPut("/messages/{messageId}/receipt", async (HttpContext c, string messageId, MessageReceiptRequest body, ICoreService s, CancellationToken ct) => Results.Ok(await s.SaveMessageReceiptAsync(Member(c), messageId, body, Idempotency(c), ct)));
v1.MapPatch("/me/notification-preferences", async (HttpContext c, NotificationPreferenceRequest body, ICoreService s, CancellationToken ct) => Results.Ok(await s.SetNotificationPreferenceAsync(Member(c), body, ct)));
v1.MapPost("/me/privacy-requests", async (HttpContext c, PrivacyRequestCreate body, ICoreService s, CancellationToken ct) => Results.Accepted("/v1/me/privacy-requests", await s.CreatePrivacyRequestAsync(Member(c), body, ct)));
v1.MapGet("/sync/changes", async (HttpContext c, string? cursor, int? limit, ICoreService s, CancellationToken ct) => Results.Ok(await s.GetChangesAsync(Member(c), DecodeCursor(cursor), limit ?? 100, ct)));

if (local) await LocalDevelopmentSeeder.SeedAsync(app.Services, CancellationToken.None);
app.Run();

static string Member(HttpContext context)
{
    var id = context.User.FindFirst("sub")?.Value;
    return !string.IsNullOrWhiteSpace(id) ? id : throw new DomainException("MEMBER_IDENTITY_REQUIRED", 401);
}

static string Idempotency(HttpContext context) => context.Request.Headers["Idempotency-Key"].ToString();

static long DecodeCursor(string? cursor)
{
    if (string.IsNullOrWhiteSpace(cursor)) return 0;
    try
    {
        var value = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        return long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var sequence) && sequence >= 0
            ? sequence
            : throw new FormatException();
    }
    catch (FormatException) { throw new DomainException("SYNC_CURSOR_INVALID"); }
}

static async Task Error(HttpContext context, int status, string code)
{
    if (context.Response.HasStarted) return;
    context.Response.StatusCode = status; context.Response.ContentType = "application/problem+json";
    await context.Response.WriteAsJsonAsync(new ApiError(code, code switch { "MEMBER_IDENTITY_REQUIRED" => "An authenticated member identity is required.", "IDEMPOTENCY_KEY_REQUIRED" => "An Idempotency-Key header is required for every mutation.", "IF_MATCH_REQUIRED" => "An If-Match header is required.", "RESOURCE_VERSION_CONFLICT" => "The resource changed since it was read.", "INTERNAL_ERROR" => "The request could not be completed.", _ => "The request is invalid or cannot be completed in its current state." }, context.TraceIdentifier));
}

public partial class Program { }
