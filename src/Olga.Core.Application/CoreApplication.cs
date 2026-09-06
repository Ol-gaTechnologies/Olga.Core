using System.Text.Json;
using Olga.Core.Contracts;
using Olga.Core.Domain;

namespace Olga.Core.Application;

public interface ICoreStore
{
    IQueryable<MemberProfile> Profiles { get; }
    IQueryable<MemberConsent> Consents { get; }
    IQueryable<EventRecord> Events { get; }
    IQueryable<EventRegistration> Registrations { get; }
    IQueryable<LiveModeSession> LiveSessions { get; }
    IQueryable<EventPresence> Presence { get; }
    IQueryable<ConnectionRequest> ConnectionRequests { get; }
    IQueryable<Connection> Connections { get; }
    IQueryable<MemberBlock> Blocks { get; }
    IQueryable<Conversation> Conversations { get; }
    IQueryable<Message> Messages { get; }
    IQueryable<NotificationPreference> NotificationPreferences { get; }
    IQueryable<PrivacyRequest> PrivacyRequests { get; }
    IQueryable<SyncChange> SyncChanges { get; }
    void Add<T>(T entity) where T : class;
    void Remove<T>(T entity) where T : class;
    Task SaveAsync(CancellationToken ct);
}

public sealed record NlpEligibilityProjection(string MemberId, string ContextId, bool Eligible, string ReasonCode);
public sealed record NlpRelationshipProjection(string RequesterId, string CandidateId, bool Connected, bool Blocked);

public interface ICoreService
{
    Task<ProfileResponse> GetOwnProfileAsync(string memberId, CancellationToken ct);
    Task<ProfileResponse> GetVisibleProfileAsync(string actorId, string memberId, CancellationToken ct);
    Task<ProfileResponse> UpdateProfileAsync(string memberId, ProfileUpdateRequest request, string? ifMatch, CancellationToken ct);
    Task<ConsentResponse> RecordConsentAsync(string memberId, ConsentRequest request, CancellationToken ct);
    Task<IReadOnlyList<EventResponse>> GetEventsAsync(CancellationToken ct);
    Task<RegistrationResponse> RegisterAsync(string memberId, string eventId, CancellationToken ct);
    Task<LiveModeResponse> StartLiveModeAsync(string memberId, string eventId, LiveModeRequest request, CancellationToken ct);
    Task StopLiveModeAsync(string memberId, string eventId, CancellationToken ct);
    Task RecordPresenceAsync(string memberId, string eventId, PresenceRequest request, CancellationToken ct);
    Task<ConnectionRequestResponse> CreateConnectionRequestAsync(string senderId, ConnectionRequestCreate request, CancellationToken ct);
    Task<ConnectionResponse> DecideConnectionRequestAsync(string memberId, string requestId, ConnectionDecisionRequest request, CancellationToken ct);
    Task BlockAsync(string memberId, BlockRequest request, CancellationToken ct);
    Task<IReadOnlyList<ConnectionResponse>> GetConnectionsAsync(string memberId, CancellationToken ct);
    Task<MessageResponse> SendMessageAsync(string memberId, string conversationId, MessageCreateRequest request, CancellationToken ct);
    Task<IReadOnlyList<MessageResponse>> GetMessagesAsync(string memberId, string conversationId, long after, int limit, CancellationToken ct);
    Task<NotificationPreferenceResponse> SetNotificationPreferenceAsync(string memberId, NotificationPreferenceRequest request, CancellationToken ct);
    Task<PrivacyRequestResponse> CreatePrivacyRequestAsync(string memberId, PrivacyRequestCreate request, CancellationToken ct);
    Task<SyncResponse> GetChangesAsync(string memberId, long after, int limit, CancellationToken ct);
    Task<NlpEligibilityProjection> GetNlpEligibilityAsync(string memberId, string contextId, CancellationToken ct);
    Task<NlpRelationshipProjection> GetNlpRelationshipAsync(string requesterId, string candidateId, CancellationToken ct);
}

public sealed class CoreService(ICoreStore store) : ICoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public Task<ProfileResponse> GetOwnProfileAsync(string memberId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Map(FindProfile(memberId)));
    }

    public Task<ProfileResponse> GetVisibleProfileAsync(string actorId, string memberId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var profile = FindProfile(memberId);
        if (profile.Visibility == "PRIVATE" && actorId != memberId && !IsConnected(actorId, memberId))
            throw new DomainException("PROFILE_NOT_FOUND", 404);
        return Task.FromResult(Map(profile));
    }

    public async Task<ProfileResponse> UpdateProfileAsync(string memberId, ProfileUpdateRequest request, string? ifMatch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 200) throw new DomainException("PROFILE_INVALID");
        if (request.Visibility is not ("PUBLIC" or "MEMBERS" or "PRIVATE")) throw new DomainException("PROFILE_VISIBILITY_INVALID");
        var profile = store.Profiles.SingleOrDefault(x => x.MemberId == memberId);
        if (profile is null)
        {
            if (!string.IsNullOrWhiteSpace(ifMatch)) throw new DomainException("RESOURCE_VERSION_CONFLICT", 409);
            profile = new MemberProfile { MemberId = memberId };
            store.Add(profile);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(ifMatch)) throw new DomainException("IF_MATCH_REQUIRED", 428);
            if (!string.Equals(ifMatch.Trim('"'), profile.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
                throw new DomainException("RESOURCE_VERSION_CONFLICT", 409);
            profile.Version++;
        }
        profile.DisplayName = request.DisplayName.Trim();
        profile.Headline = request.Headline?.Trim();
        profile.Biography = request.Biography?.Trim();
        profile.Organization = request.Organization?.Trim();
        profile.Sector = request.Sector?.Trim();
        profile.Geography = request.Geography?.Trim();
        profile.Visibility = request.Visibility;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        AddChange(memberId, "PROFILE", memberId, "UPSERT", new { profile.DisplayName, profile.Headline, profile.Organization, profile.Sector, profile.Geography, profile.Visibility, profile.Version });
        AddOutbox("MEMBER", memberId, "MemberProfileChanged.v1", new { member_id = memberId, profile.Version });
        await store.SaveAsync(ct);
        return Map(profile);
    }

    public async Task<ConsentResponse> RecordConsentAsync(string memberId, ConsentRequest request, CancellationToken ct)
    {
        if (request.Decision is not ("GRANTED" or "WITHDRAWN" or "DENIED") || string.IsNullOrWhiteSpace(request.PurposeCode))
            throw new DomainException("CONSENT_INVALID");
        var row = new MemberConsent { MemberId = memberId, PurposeCode = request.PurposeCode, PolicyVersion = request.PolicyVersion, Decision = request.Decision };
        store.Add(row);
        if (request.PurposeCode == "LIVE_MODE" && request.Decision != "GRANTED")
            foreach (var session in store.LiveSessions.Where(x => x.MemberId == memberId && x.Status == "ACTIVE").ToArray()) { session.Status = "REVOKED"; session.RevokedAt = DateTimeOffset.UtcNow; }
        AddChange(memberId, "CONSENT", request.PurposeCode, "UPSERT", new { request.PurposeCode, request.PolicyVersion, request.Decision, row.CapturedAt });
        AddOutbox("MEMBER", memberId, "MemberConsentChanged.v1", new { member_id = memberId, purpose_code = request.PurposeCode, decision = request.Decision });
        await store.SaveAsync(ct);
        return new(request.PurposeCode, request.PolicyVersion, request.Decision, row.CapturedAt);
    }

    public Task<IReadOnlyList<EventResponse>> GetEventsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<EventResponse> result = store.Events.Where(x => x.Status == "PUBLISHED").OrderBy(x => x.StartsAt).Select(Map).ToArray();
        return Task.FromResult(result);
    }

    public async Task<RegistrationResponse> RegisterAsync(string memberId, string eventId, CancellationToken ct)
    {
        _ = FindEvent(eventId);
        var row = store.Registrations.SingleOrDefault(x => x.EventId == eventId && x.MemberId == memberId);
        if (row is null) { row = new EventRegistration { EventId = eventId, MemberId = memberId }; store.Add(row); }
        AddChange(memberId, "EVENT_REGISTRATION", eventId, "UPSERT", new { event_id = eventId, status = row.Status });
        AddOutbox("EVENT_REGISTRATION", $"{eventId}:{memberId}", "EventRegistrationChanged.v1", new { event_id = eventId, member_id = memberId, status = row.Status });
        await store.SaveAsync(ct);
        return new(row.EventId, row.MemberId, row.Status, row.RegisteredAt);
    }

    public async Task<LiveModeResponse> StartLiveModeAsync(string memberId, string eventId, LiveModeRequest request, CancellationToken ct)
    {
        var evt = FindEvent(eventId);
        if (request.DurationMinutes is < 5 or > 240) throw new DomainException("LIVE_MODE_DURATION_INVALID");
        if (!store.Registrations.Any(x => x.EventId == eventId && x.MemberId == memberId && x.Status == "REGISTERED")) throw new DomainException("EVENT_REGISTRATION_REQUIRED", 403);
        if (!HasConsent(memberId, "LIVE_MODE")) throw new DomainException("LIVE_MODE_CONSENT_REQUIRED", 403);
        var now = DateTimeOffset.UtcNow;
        if (evt.EndsAt <= now) throw new DomainException("EVENT_NOT_ACTIVE", 409);
        var existing = store.LiveSessions.SingleOrDefault(x => x.EventId == eventId && x.MemberId == memberId && x.Status == "ACTIVE");
        if (existing is not null) { existing.ActiveUntil = Min(now.AddMinutes(request.DurationMinutes), evt.EndsAt); await store.SaveAsync(ct); return Map(existing); }
        var row = new LiveModeSession { EventId = eventId, MemberId = memberId, ActiveUntil = Min(now.AddMinutes(request.DurationMinutes), evt.EndsAt) };
        store.Add(row);
        AddChange(memberId, "LIVE_MODE", eventId, "UPSERT", new { row.SessionId, event_id = eventId, row.Status, row.ActiveUntil });
        AddOutbox("LIVE_MODE", row.SessionId, "LiveModeChanged.v1", new { session_id = row.SessionId, event_id = eventId, member_id = memberId, row.Status, row.ActiveUntil });
        await store.SaveAsync(ct);
        return Map(row);
    }

    public async Task StopLiveModeAsync(string memberId, string eventId, CancellationToken ct)
    {
        var session = store.LiveSessions.SingleOrDefault(x => x.EventId == eventId && x.MemberId == memberId && x.Status == "ACTIVE") ?? throw new DomainException("LIVE_MODE_NOT_ACTIVE", 404);
        session.Status = "REVOKED"; session.RevokedAt = DateTimeOffset.UtcNow;
        AddChange(memberId, "LIVE_MODE", eventId, "DELETE", null);
        AddOutbox("LIVE_MODE", session.SessionId, "LiveModeChanged.v1", new { session_id = session.SessionId, event_id = eventId, member_id = memberId, session.Status });
        await store.SaveAsync(ct);
    }

    public async Task RecordPresenceAsync(string memberId, string eventId, PresenceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CoarseCell) || request.CoarseCell.Length > 64) throw new DomainException("PRESENCE_INVALID");
        var session = store.LiveSessions.SingleOrDefault(x => x.EventId == eventId && x.MemberId == memberId && x.Status == "ACTIVE" && x.ActiveUntil > DateTimeOffset.UtcNow)
            ?? throw new DomainException("LIVE_MODE_NOT_ACTIVE", 403);
        var observed = request.ObservedAt == default ? DateTimeOffset.UtcNow : request.ObservedAt;
        if (Math.Abs((DateTimeOffset.UtcNow - observed).TotalMinutes) > 10) throw new DomainException("PRESENCE_STALE");
        store.Add(new EventPresence { SessionId = session.SessionId, CoarseCell = request.CoarseCell, ObservedAt = observed, ExpiresAt = Min(session.ActiveUntil, observed.AddHours(2)) });
        AddOutbox("LIVE_MODE", session.SessionId, "EventPresenceRefreshed.v1", new { session_id = session.SessionId, event_id = eventId, member_id = memberId, expires_at = Min(session.ActiveUntil, observed.AddHours(2)) });
        await store.SaveAsync(ct);
    }

    public async Task<ConnectionRequestResponse> CreateConnectionRequestAsync(string senderId, ConnectionRequestCreate request, CancellationToken ct)
    {
        if (senderId == request.RecipientMemberId) throw new DomainException("SELF_CONNECTION_INVALID");
        _ = FindProfile(request.RecipientMemberId);
        if (IsBlocked(senderId, request.RecipientMemberId)) throw new DomainException("CONNECTION_NOT_ALLOWED", 403);
        if (IsConnected(senderId, request.RecipientMemberId)) throw new DomainException("CONNECTION_EXISTS", 409);
        var existing = store.ConnectionRequests.SingleOrDefault(x => x.Status == "PENDING" && ((x.SenderMemberId == senderId && x.RecipientMemberId == request.RecipientMemberId) || (x.SenderMemberId == request.RecipientMemberId && x.RecipientMemberId == senderId)));
        if (existing is not null) return Map(existing);
        var row = new ConnectionRequest { SenderMemberId = senderId, RecipientMemberId = request.RecipientMemberId, ExpiresAt = DateTimeOffset.UtcNow.AddDays(Math.Clamp(request.ExpiresInDays, 1, 30)) };
        store.Add(row);
        AddChange(request.RecipientMemberId, "CONNECTION_REQUEST", row.RequestId, "UPSERT", new { row.RequestId, row.SenderMemberId, row.Status, row.ExpiresAt });
        AddOutbox("CONNECTION_REQUEST", row.RequestId, "ConnectionRequestCreated.v1", new { request_id = row.RequestId, sender_member_id = senderId, recipient_member_id = request.RecipientMemberId });
        await store.SaveAsync(ct);
        return Map(row);
    }

    public async Task<ConnectionResponse> DecideConnectionRequestAsync(string memberId, string requestId, ConnectionDecisionRequest request, CancellationToken ct)
    {
        var row = store.ConnectionRequests.SingleOrDefault(x => x.RequestId == requestId && x.RecipientMemberId == memberId) ?? throw new DomainException("CONNECTION_REQUEST_NOT_FOUND", 404);
        if (row.Status != "PENDING" || row.ExpiresAt <= DateTimeOffset.UtcNow) throw new DomainException("CONNECTION_REQUEST_NOT_PENDING", 409);
        if (request.Decision is not ("ACCEPT" or "DECLINE")) throw new DomainException("CONNECTION_DECISION_INVALID");
        if (request.Decision == "DECLINE") { row.Status = "DECLINED"; await store.SaveAsync(ct); return new("", row.SenderMemberId, row.Status, ""); }
        if (IsBlocked(row.SenderMemberId, row.RecipientMemberId)) throw new DomainException("CONNECTION_NOT_ALLOWED", 403);
        row.Status = "ACCEPTED";
        var pair = Pair(row.SenderMemberId, row.RecipientMemberId);
        var connection = new Connection { MemberLowId = pair.Low, MemberHighId = pair.High };
        var conversation = new Conversation { ConnectionId = connection.ConnectionId };
        store.Add(connection); store.Add(conversation);
        foreach (var id in new[] { row.SenderMemberId, row.RecipientMemberId }) AddChange(id, "CONNECTION", connection.ConnectionId, "UPSERT", new { connection.ConnectionId, member_id = id == row.SenderMemberId ? row.RecipientMemberId : row.SenderMemberId, conversation.ConversationId, connection.Status });
        AddOutbox("CONNECTION", connection.ConnectionId, "ConnectionAccepted.v1", new { connection_id = connection.ConnectionId, member_low_id = pair.Low, member_high_id = pair.High, conversation_id = conversation.ConversationId });
        await store.SaveAsync(ct);
        return new(connection.ConnectionId, row.SenderMemberId, connection.Status, conversation.ConversationId);
    }

    public async Task BlockAsync(string memberId, BlockRequest request, CancellationToken ct)
    {
        if (memberId == request.MemberId) throw new DomainException("SELF_BLOCK_INVALID");
        if (!store.Blocks.Any(x => x.BlockerMemberId == memberId && x.BlockedMemberId == request.MemberId && x.RemovedAt == null)) store.Add(new MemberBlock { BlockerMemberId = memberId, BlockedMemberId = request.MemberId });
        foreach (var connection in store.Connections.Where(x => x.Status == "ACTIVE" && ((x.MemberLowId == memberId && x.MemberHighId == request.MemberId) || (x.MemberLowId == request.MemberId && x.MemberHighId == memberId))).ToArray()) connection.Status = "BLOCKED";
        foreach (var id in new[] { memberId, request.MemberId }) AddChange(id, "CONNECTION", PairKey(memberId, request.MemberId), "DELETE", null);
        AddOutbox("MEMBER_RELATIONSHIP", PairKey(memberId, request.MemberId), "MemberBlocked.v1", new { actor_member_id = memberId, target_member_id = request.MemberId });
        await store.SaveAsync(ct);
    }

    public Task<IReadOnlyList<ConnectionResponse>> GetConnectionsAsync(string memberId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = store.Connections.Where(x => x.Status == "ACTIVE" && (x.MemberLowId == memberId || x.MemberHighId == memberId)).AsEnumerable().Select(x => new ConnectionResponse(x.ConnectionId, x.MemberLowId == memberId ? x.MemberHighId : x.MemberLowId, x.Status, store.Conversations.Single(c => c.ConnectionId == x.ConnectionId).ConversationId)).ToArray();
        return Task.FromResult<IReadOnlyList<ConnectionResponse>>(result);
    }

    public async Task<MessageResponse> SendMessageAsync(string memberId, string conversationId, MessageCreateRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MessageId) || string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 4000) throw new DomainException("MESSAGE_INVALID");
        var conversation = store.Conversations.SingleOrDefault(x => x.ConversationId == conversationId) ?? throw new DomainException("CONVERSATION_NOT_FOUND", 404);
        var connection = store.Connections.Single(x => x.ConnectionId == conversation.ConnectionId);
        if (connection.Status != "ACTIVE" || (connection.MemberLowId != memberId && connection.MemberHighId != memberId)) throw new DomainException("CONVERSATION_FORBIDDEN", 403);
        var other = connection.MemberLowId == memberId ? connection.MemberHighId : connection.MemberLowId;
        if (IsBlocked(memberId, other)) throw new DomainException("CONVERSATION_FORBIDDEN", 403);
        var existing = store.Messages.SingleOrDefault(x => x.MessageId == request.MessageId);
        if (existing is not null) return Map(existing);
        var sequence = store.Messages.Where(x => x.ConversationId == conversationId).Select(x => x.ServerSequence).DefaultIfEmpty().Max() + 1;
        var row = new Message { MessageId = request.MessageId, ConversationId = conversationId, SenderMemberId = memberId, Body = request.Body, ServerSequence = sequence };
        store.Add(row);
        foreach (var id in new[] { memberId, other }) AddChange(id, "MESSAGE", row.MessageId, "UPSERT", new { row.MessageId, row.ConversationId, row.SenderMemberId, row.Body, row.ServerSequence, row.CreatedAt });
        AddOutbox("MESSAGE", row.MessageId, "MessageCreated.v1", new { message_id = row.MessageId, conversation_id = conversationId, sender_member_id = memberId, recipient_member_id = other, row.ServerSequence });
        await store.SaveAsync(ct);
        return Map(row);
    }

    public Task<IReadOnlyList<MessageResponse>> GetMessagesAsync(string memberId, string conversationId, long after, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _ = RequireConversationMember(memberId, conversationId);
        IReadOnlyList<MessageResponse> result = store.Messages.Where(x => x.ConversationId == conversationId && x.ServerSequence > after).OrderBy(x => x.ServerSequence).Take(Math.Clamp(limit, 1, 100)).AsEnumerable().Select(Map).ToArray();
        return Task.FromResult(result);
    }

    public async Task<NotificationPreferenceResponse> SetNotificationPreferenceAsync(string memberId, NotificationPreferenceRequest request, CancellationToken ct)
    {
        if (request.Channel is not ("PUSH" or "EMAIL") || string.IsNullOrWhiteSpace(request.PurposeCode)) throw new DomainException("NOTIFICATION_PREFERENCE_INVALID");
        var row = store.NotificationPreferences.SingleOrDefault(x => x.MemberId == memberId && x.PurposeCode == request.PurposeCode);
        if (row is null) { row = new NotificationPreference { MemberId = memberId, PurposeCode = request.PurposeCode }; store.Add(row); }
        row.Channel = request.Channel; row.Enabled = request.Enabled; row.UpdatedAt = DateTimeOffset.UtcNow;
        AddChange(memberId, "NOTIFICATION_PREFERENCE", request.PurposeCode, "UPSERT", new { request.PurposeCode, request.Channel, request.Enabled, row.UpdatedAt });
        await store.SaveAsync(ct);
        return new(row.PurposeCode, row.Channel, row.Enabled, row.UpdatedAt);
    }

    public async Task<PrivacyRequestResponse> CreatePrivacyRequestAsync(string memberId, PrivacyRequestCreate request, CancellationToken ct)
    {
        if (request.RequestType is not ("ACCESS" or "CORRECTION" or "DELETION")) throw new DomainException("PRIVACY_REQUEST_INVALID");
        var row = new PrivacyRequest { MemberId = memberId, RequestType = request.RequestType, DueAt = DateTimeOffset.UtcNow.AddDays(30) };
        store.Add(row);
        AddChange(memberId, "PRIVACY_REQUEST", row.PrivacyRequestId, "UPSERT", new { row.PrivacyRequestId, row.RequestType, row.Status, row.CreatedAt, row.DueAt });
        AddOutbox("PRIVACY_REQUEST", row.PrivacyRequestId, "PrivacyRequestCreated.v1", new { privacy_request_id = row.PrivacyRequestId, member_id = memberId, row.RequestType });
        await store.SaveAsync(ct);
        return Map(row);
    }

    public Task<SyncResponse> GetChangesAsync(string memberId, long after, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bounded = Math.Clamp(limit, 1, 200);
        var query = store.SyncChanges.Where(x => x.SyncSequence > after && (x.MemberScopeId == null || x.MemberScopeId == memberId)).OrderBy(x => x.SyncSequence);
        var rows = query.Take(bounded + 1).ToArray();
        var hasMore = rows.Length > bounded;
        var page = rows.Take(bounded).Select(x => new SyncItem(x.SyncSequence, x.ResourceType, x.ResourceId, x.ChangeType, x.PayloadJson is null ? null : JsonSerializer.Deserialize<object>(x.PayloadJson, JsonOptions), x.OccurredAt)).ToArray();
        return Task.FromResult(new SyncResponse(page, page.LastOrDefault()?.Sequence ?? after, hasMore));
    }

    public Task<NlpEligibilityProjection> GetNlpEligibilityAsync(string memberId, string contextId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var profile = store.Profiles.SingleOrDefault(x => x.MemberId == memberId);
        if (profile is null || profile.Status != "ACTIVE") return Task.FromResult(new NlpEligibilityProjection(memberId, contextId, false, "MEMBER_INACTIVE"));
        if (!store.Registrations.Any(x => x.MemberId == memberId && x.EventId == contextId && x.Status == "REGISTERED")) return Task.FromResult(new NlpEligibilityProjection(memberId, contextId, false, "NOT_REGISTERED"));
        if (!HasConsent(memberId, "MATCHING")) return Task.FromResult(new NlpEligibilityProjection(memberId, contextId, false, "CONSENT_REQUIRED"));
        if (!store.LiveSessions.Any(x => x.MemberId == memberId && x.EventId == contextId && x.Status == "ACTIVE" && x.ActiveUntil > DateTimeOffset.UtcNow)) return Task.FromResult(new NlpEligibilityProjection(memberId, contextId, false, "LIVE_MODE_INACTIVE"));
        return Task.FromResult(new NlpEligibilityProjection(memberId, contextId, true, "ELIGIBLE"));
    }

    public Task<NlpRelationshipProjection> GetNlpRelationshipAsync(string requesterId, string candidateId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new NlpRelationshipProjection(requesterId, candidateId, IsConnected(requesterId, candidateId), IsBlocked(requesterId, candidateId)));
    }

    private MemberProfile FindProfile(string id) => store.Profiles.SingleOrDefault(x => x.MemberId == id && x.Status == "ACTIVE") ?? throw new DomainException("PROFILE_NOT_FOUND", 404);
    private EventRecord FindEvent(string id) => store.Events.SingleOrDefault(x => x.EventId == id && x.Status == "PUBLISHED") ?? throw new DomainException("EVENT_NOT_FOUND", 404);
    private bool HasConsent(string memberId, string purpose) => store.Consents.Where(x => x.MemberId == memberId && x.PurposeCode == purpose).OrderByDescending(x => x.CapturedAt).Select(x => x.Decision).FirstOrDefault() == "GRANTED";
    private bool IsBlocked(string a, string b) => store.Blocks.Any(x => x.RemovedAt == null && ((x.BlockerMemberId == a && x.BlockedMemberId == b) || (x.BlockerMemberId == b && x.BlockedMemberId == a)));
    private bool IsConnected(string a, string b) { var p = Pair(a, b); return store.Connections.Any(x => x.MemberLowId == p.Low && x.MemberHighId == p.High && x.Status == "ACTIVE"); }
    private Connection RequireConversationMember(string memberId, string conversationId) { var c = store.Conversations.SingleOrDefault(x => x.ConversationId == conversationId) ?? throw new DomainException("CONVERSATION_NOT_FOUND", 404); var link = store.Connections.Single(x => x.ConnectionId == c.ConnectionId); return link.Status == "ACTIVE" && (link.MemberLowId == memberId || link.MemberHighId == memberId) ? link : throw new DomainException("CONVERSATION_FORBIDDEN", 403); }
    private void AddChange(string? memberId, string type, string id, string change, object? payload) => store.Add(new SyncChange { MemberScopeId = memberId, ResourceType = type, ResourceId = id, ChangeType = change, PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload, JsonOptions) });
    private void AddOutbox(string aggregateType, string aggregateId, string eventType, object payload) => store.Add(new OutboxEvent { AggregateType = aggregateType, AggregateId = aggregateId, EventType = eventType, PayloadJson = JsonSerializer.Serialize(payload, JsonOptions) });
    private static (string Low, string High) Pair(string a, string b) => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
    private static string PairKey(string a, string b) { var p = Pair(a, b); return $"{p.Low}:{p.High}"; }
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
    private static ProfileResponse Map(MemberProfile x) => new(x.MemberId, x.DisplayName, x.Headline, x.Biography, x.Organization, x.Sector, x.Geography, x.Visibility, $"\"{x.Version}\"", x.UpdatedAt);
    private static EventResponse Map(EventRecord x) => new(x.EventId, x.Name, x.StartsAt, x.EndsAt, x.Status);
    private static LiveModeResponse Map(LiveModeSession x) => new(x.SessionId, x.EventId, x.Status, x.ActiveUntil);
    private static ConnectionRequestResponse Map(ConnectionRequest x) => new(x.RequestId, x.SenderMemberId, x.RecipientMemberId, x.Status, x.ExpiresAt);
    private static MessageResponse Map(Message x) => new(x.MessageId, x.ConversationId, x.SenderMemberId, x.Body, x.ServerSequence, x.CreatedAt);
    private static PrivacyRequestResponse Map(PrivacyRequest x) => new(x.PrivacyRequestId, x.RequestType, x.Status, x.CreatedAt, x.DueAt);
}
