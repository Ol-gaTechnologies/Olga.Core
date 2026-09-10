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
- Read-only internal NLP eligibility and relationship projections. Core remains the authority; NLP cannot change or bypass these decisions.
- EF Core InMemory local development and PostgreSQL configuration through `ConnectionStrings__PostgreSql`.

## What is intentionally not implemented yet

This foundation is not the full product backlog. CIAM/OIDC token validation, permissions, private file lifecycle, message receipts, notification delivery, privacy task orchestration, retention execution, moderation/admin APIs, database migrations, Service Bus publishing, OpenTelemetry, and production deployment assets remain delivery work. See [Senior architecture review](docs/SENIOR_ARCHITECT_REVIEW.md).

## Run locally

```powershell
dotnet restore Olga.Core.slnx
dotnet test Olga.Core.slnx
dotnet run --project src/Olga.Core.Api
```

Development accepts `X-Member-Id` as a local-only identity substitute and seeds members `A123`, `B456`, `D111` plus `event-001`. Production must disable that header and configure real authentication.

## Endpoint groups

- Profile and consent: `GET/PATCH /v1/me/profile`, `GET /v1/members/{memberId}`, `POST /v1/me/consents`
- Events: `GET /v1/events`, `POST /v1/events/{id}/register`, `POST/DELETE /v1/events/{id}/live-mode`, `POST /v1/events/{id}/presence`
- Social: `POST/PATCH /v1/connection-requests`, `GET /v1/connections`, `POST /v1/members/block`
- Chat: `GET/POST /v1/conversations/{id}/messages`
- Preferences and privacy: `PATCH /v1/me/notification-preferences`, `POST /v1/me/privacy-requests`
- Offline sync: `GET /v1/sync/changes?after={cursor}&limit={n}`
- NLP boundary: `GET /v1/internal/nlp/eligibility/{contextId}/{memberId}`, `GET /v1/internal/nlp/relationships/{requesterId}/{candidateId}`
- Operations: `GET /health`, `GET /ready`, `GET /openapi/v1.json`

## Developer orientation

Read [Architecture and data flow](ARCHITECTURE.md) before changing domain ownership or adding an endpoint. It explains each project, the main request flows, persistence schemas, cross-API events, and the rules that must remain true.

The pull-request and environment deployment process is documented in [CI/CD operations](docs/CI_CD.md).
