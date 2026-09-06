namespace Olga.Core.Domain;

public sealed class MemberProfile
{
    public string MemberId { get; set; } = "";
    public string CommunityId { get; set; } = "olga";
    public string DisplayName { get; set; } = "";
    public string? Headline { get; set; }
    public string? Biography { get; set; }
    public string? Organization { get; set; }
    public string? Sector { get; set; }
    public string? Geography { get; set; }
    public string Visibility { get; set; } = "MEMBERS";
    public string Status { get; set; } = "ACTIVE";
    public long Version { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MemberConsent
{
    public long Id { get; set; }
    public string MemberId { get; set; } = "";
    public string PurposeCode { get; set; } = "";
    public string PolicyVersion { get; set; } = "1";
    public string Decision { get; set; } = "GRANTED";
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class EventRecord
{
    public string EventId { get; set; } = "";
    public string CommunityId { get; set; } = "olga";
    public string Name { get; set; } = "";
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string Status { get; set; } = "PUBLISHED";
}

public sealed class EventRegistration
{
    public long Id { get; set; }
    public string EventId { get; set; } = "";
    public string MemberId { get; set; } = "";
    public string Status { get; set; } = "REGISTERED";
    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LiveModeSession
{
    public string SessionId { get; set; } = Guid.NewGuid().ToString("N");
    public string EventId { get; set; } = "";
    public string MemberId { get; set; } = "";
    public string Status { get; set; } = "ACTIVE";
    public DateTimeOffset ActiveUntil { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class EventPresence
{
    public long Id { get; set; }
    public string SessionId { get; set; } = "";
    public string CoarseCell { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ConnectionRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string SenderMemberId { get; set; } = "";
    public string RecipientMemberId { get; set; } = "";
    public string Status { get; set; } = "PENDING";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class Connection
{
    public string ConnectionId { get; set; } = Guid.NewGuid().ToString("N");
    public string MemberLowId { get; set; } = "";
    public string MemberHighId { get; set; } = "";
    public string Status { get; set; } = "ACTIVE";
    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MemberBlock
{
    public long Id { get; set; }
    public string BlockerMemberId { get; set; } = "";
    public string BlockedMemberId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RemovedAt { get; set; }
}

public sealed class Conversation
{
    public string ConversationId { get; set; } = Guid.NewGuid().ToString("N");
    public string ConnectionId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Message
{
    public string MessageId { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string SenderMemberId { get; set; } = "";
    public string Body { get; set; } = "";
    public long ServerSequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class NotificationPreference
{
    public string MemberId { get; set; } = "";
    public string PurposeCode { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Channel { get; set; } = "PUSH";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PrivacyRequest
{
    public string PrivacyRequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string MemberId { get; set; } = "";
    public string RequestType { get; set; } = "ACCESS";
    public string Status { get; set; } = "OPEN";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset DueAt { get; set; }
}

public sealed class SyncChange
{
    public long SyncSequence { get; set; }
    public string CommunityId { get; set; } = "olga";
    public string? MemberScopeId { get; set; }
    public string ResourceType { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string ChangeType { get; set; } = "UPSERT";
    public string? PayloadJson { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddDays(30);
}

public sealed class OutboxEvent
{
    public string OutboxEventId { get; set; } = Guid.NewGuid().ToString("N");
    public string AggregateType { get; set; } = "";
    public string AggregateId { get; set; } = "";
    public string EventType { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
    public int AttemptCount { get; set; }
}

public sealed class DomainException(string code, int statusCode = 400) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
