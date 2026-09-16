# OLGA Connect Core API

The Core API is the authoritative product API for OLGA Connect. It owns member-facing product state and policy enforcement. It deliberately contains no text normalization, embedding, semantic ranking, model evaluation, or other NLP/AI implementation.

The repository is independent from `Olga.Nlp` and can be versioned, built, tested, deployed, and granted database permissions separately. During the MVP both APIs may use one PostgreSQL 17 database hosted on Azure Database for PostgreSQL Flexible Server, but each runtime must receive least-privilege access only to its owned schemas, views, and functions.

## What is implemented

- Profile reads and optimistic-concurrency updates with ETags.
- Append-only consent decisions, including immediate Live Mode revocation.
- Event listing, registration, bounded Live Mode, and expiring coarse presence.
- Connection request, acceptance, canonical connection, conversation creation, and blocking.
- Idempotent client message IDs and server message ordering.
- Notification preferences, privacy-request initiation, and authorization-filtered sync changes.
- Transactional outbox records for cross-domain and NLP-related integration events.
- Transactional integration events that allow NLP to refresh its read-only eligibility projections without proxying NLP requests through Core.
- EF Core InMemory local development and PostgreSQL configuration through `ConnectionStrings__PostgreSql`.
- OIDC/JWT bearer validation for protected API operations, with Swagger authorization support.

## What is intentionally not implemented yet

This foundation is not the full product backlog. CIAM lifecycle integration, permissions, private file lifecycle, message receipts, notification delivery, privacy task orchestration, retention execution, moderation/admin APIs, database migrations, Service Bus publishing, OpenTelemetry, and production deployment assets remain delivery work. See [Senior architecture review](docs/SENIOR_ARCHITECT_REVIEW.md).

## Run locally

```powershell
dotnet restore Olga.Core.slnx
dotnet test Olga.Core.slnx
dotnet run --project src/Olga.Core.Api
```

Protected endpoints accept `Authorization: Bearer <JWT>`. Outside Development, the API requires `Identity__Authority` and `Identity__Audience`; metadata retrieval remains HTTPS-only. Development may also accept a raw member ID in `X-Member-Id` when `Identity__AllowLocalMemberHeader=true`, and seeds members `A123`, `B456`, `D111` plus `event-001`. This local scheme is not registered outside Development.

`GET /v1/events`, `/health`, `/ready`, Swagger UI, and the OpenAPI document are intentionally anonymous. Every other `/v1` operation requires an authenticated subject. Swagger presents only the schemes registered for the current environment and derives operation locks from the same authorization metadata enforced at runtime.

## Endpoint groups

- Profile and consent: `GET/PATCH /v1/me/profile`, `GET /v1/members/{memberId}`, `POST /v1/me/consents`
- Events: `GET /v1/events`, `POST /v1/events/{id}/register`, `POST/DELETE /v1/events/{id}/live-mode`, `POST /v1/events/{id}/presence`
- Social: `POST/PATCH /v1/connection-requests`, `GET /v1/connections`, `POST /v1/members/block`
- Chat: `GET/POST /v1/conversations/{id}/messages`
- Preferences and privacy: `PATCH /v1/me/notification-preferences`, `POST /v1/me/privacy-requests`
- Offline sync: `GET /v1/sync/changes?after={cursor}&limit={n}`
- Operations: `GET /health`, `GET /ready`, `GET /swagger/v1/swagger.json`, Swagger UI at `/swagger`

## Developer orientation

Read [Architecture and data flow](ARCHITECTURE.md) before changing domain ownership or adding an endpoint. It explains each project, the main request flows, persistence schemas, cross-API events, and the rules that must remain true.

## Adding a new API endpoint

This project uses ASP.NET Core Minimal APIs rather than controller classes. Add a new API operation through the following layers:

```text
HTTP route in Olga.Core.Api
  -> ICoreService/CoreService in Olga.Core.Application
  -> ICoreStore/CoreDbContext in Olga.Core.Infrastructure
  -> PostgreSQL
```

### 1. Define the API contract

Add request and response records to `src/Olga.Core.Contracts/Contracts.cs`. Do not expose domain or EF Core entities directly from an endpoint.

```csharp
public sealed record MemberSearchResponse(
    string MemberId,
    string DisplayName);
```

### 2. Declare the application operation

Add the method to `ICoreService` in `src/Olga.Core.Application/CoreApplication.cs`:

```csharp
Task<IReadOnlyList<MemberSearchResponse>> SearchMembersAsync(
    string searchText,
    CancellationToken ct);
```

### 3. Implement the operation

Implement the method in `CoreService`. Keep authorization, validation, and business rules in the application layer rather than in the HTTP route.

```csharp
public Task<IReadOnlyList<MemberSearchResponse>> SearchMembersAsync(
    string searchText,
    CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();

    var results = store.Profiles
        .Where(x => x.Status == "ACTIVE" && x.DisplayName.Contains(searchText))
        .Select(x => new MemberSearchResponse(x.MemberId, x.DisplayName))
        .ToList();

    return Task.FromResult<IReadOnlyList<MemberSearchResponse>>(results);
}
```

Use `ICoreStore` for normal data access. Add an explicit repository operation when a query or mutation needs database-specific SQL, transactional behavior, or Npgsql parameterization.

### 4. Map the HTTP route

Register the route in `src/Olga.Core.Api/Program.cs`:

```csharp
app.MapGet(
    "/v1/members/search",
    async (string query, ICoreService service, CancellationToken ct) =>
        Results.Ok(await service.SearchMembersAsync(query, ct)))
    .WithName("SearchMembers")
    .WithTags("Members");
```

All public REST routes must be explicitly versioned under `/v1`. Give each operation a unique name and a meaningful Swagger tag. New routes appear automatically in the Swagger UI at `/swagger`.

For `POST`, `PUT`, `PATCH`, and `DELETE` routes, clients must send an `Idempotency-Key` header. Use `If-Match` with the resource ETag when a mutation can lose concurrent updates. Feeds, chats, and notifications must use opaque cursor pagination.

### 5. Add tests and verify

Add application behavior tests to `tests/Olga.Core.Tests/CoreServiceTests.cs`. Cover the successful result and relevant validation, authorization, idempotency, and concurrency failures.

```powershell
dotnet test Olga.Core.slnx
dotnet run --project src/Olga.Core.Api
```

After starting the API, browse to `/swagger` to inspect and exercise the new endpoint.

The pull-request and environment deployment process is documented in [CI/CD operations](docs/CI_CD.md).
