using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Olga.Core.Application;
using Olga.Core.Domain;

namespace Olga.Core.Infrastructure;

public sealed class CoreDbContext(DbContextOptions<CoreDbContext> options) : DbContext(options), ICoreStore
{
    public DbSet<MemberProfile> MemberProfiles => Set<MemberProfile>();
    public DbSet<MemberConsent> MemberConsents => Set<MemberConsent>();
    public DbSet<EventRecord> EventRecords => Set<EventRecord>();
    public DbSet<EventRegistration> EventRegistrations => Set<EventRegistration>();
    public DbSet<LiveModeSession> LiveModeSessions => Set<LiveModeSession>();
    public DbSet<EventPresence> EventPresences => Set<EventPresence>();
    public DbSet<ConnectionRequest> SocialConnectionRequests => Set<ConnectionRequest>();
    public DbSet<Connection> SocialConnections => Set<Connection>();
    public DbSet<MemberBlock> MemberBlocks => Set<MemberBlock>();
    public DbSet<Conversation> ChatConversations => Set<Conversation>();
    public DbSet<Message> ChatMessages => Set<Message>();
    public DbSet<NotificationPreference> Preferences => Set<NotificationPreference>();
    public DbSet<PrivacyRequest> MemberPrivacyRequests => Set<PrivacyRequest>();
    public DbSet<SyncChange> Changes => Set<SyncChange>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();

    IQueryable<MemberProfile> ICoreStore.Profiles => MemberProfiles;
    IQueryable<MemberConsent> ICoreStore.Consents => MemberConsents;
    IQueryable<EventRecord> ICoreStore.Events => EventRecords;
    IQueryable<EventRegistration> ICoreStore.Registrations => EventRegistrations;
    IQueryable<LiveModeSession> ICoreStore.LiveSessions => LiveModeSessions;
    IQueryable<EventPresence> ICoreStore.Presence => EventPresences;
    IQueryable<ConnectionRequest> ICoreStore.ConnectionRequests => SocialConnectionRequests;
    IQueryable<Connection> ICoreStore.Connections => SocialConnections;
    IQueryable<MemberBlock> ICoreStore.Blocks => MemberBlocks;
    IQueryable<Conversation> ICoreStore.Conversations => ChatConversations;
    IQueryable<Message> ICoreStore.Messages => ChatMessages;
    IQueryable<NotificationPreference> ICoreStore.NotificationPreferences => Preferences;
    IQueryable<PrivacyRequest> ICoreStore.PrivacyRequests => MemberPrivacyRequests;
    IQueryable<SyncChange> ICoreStore.SyncChanges => Changes;

    void ICoreStore.Add<T>(T entity) => Set<T>().Add(entity);
    void ICoreStore.Remove<T>(T entity) => Set<T>().Remove(entity);
    Task ICoreStore.SaveAsync(CancellationToken ct) => SaveChangesAsync(ct);

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<MemberProfile>(e => { e.ToTable("MemberProfile", "core"); e.HasKey(x => x.MemberId); e.Property(x => x.MemberId).HasMaxLength(64); e.Property(x => x.CommunityId).HasMaxLength(64); e.Property(x => x.DisplayName).HasMaxLength(200); e.Property(x => x.Visibility).HasMaxLength(20); e.HasIndex(x => new { x.CommunityId, x.Status }); });
        model.Entity<MemberConsent>(e => { e.ToTable("MemberConsent", "consent"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedOnAdd(); e.Property(x => x.MemberId).HasMaxLength(64); e.Property(x => x.PurposeCode).HasMaxLength(64); e.HasIndex(x => new { x.MemberId, x.PurposeCode, x.CapturedAt }); });
        model.Entity<EventRecord>(e => { e.ToTable("Event", "event"); e.HasKey(x => x.EventId); e.Property(x => x.EventId).HasMaxLength(64); e.HasIndex(x => new { x.CommunityId, x.Status, x.StartsAt }); });
        model.Entity<EventRegistration>(e => { e.ToTable("EventRegistration", "event"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedOnAdd(); e.HasIndex(x => new { x.EventId, x.MemberId }).IsUnique(); });
        model.Entity<LiveModeSession>(e => { e.ToTable("LiveModeSession", "event"); e.HasKey(x => x.SessionId); e.Property(x => x.SessionId).HasMaxLength(64); e.HasIndex(x => new { x.EventId, x.MemberId, x.Status }); });
        model.Entity<EventPresence>(e => { e.ToTable("EventPresence", "event"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedOnAdd(); e.Property(x => x.CoarseCell).HasMaxLength(64); e.HasIndex(x => new { x.SessionId, x.ExpiresAt }); });
        model.Entity<ConnectionRequest>(e => { e.ToTable("ConnectionRequest", "social"); e.HasKey(x => x.RequestId); e.Property(x => x.RequestId).HasMaxLength(64); e.HasIndex(x => new { x.RecipientMemberId, x.Status, x.ExpiresAt }); });
        model.Entity<Connection>(e => { e.ToTable("Connection", "social"); e.HasKey(x => x.ConnectionId); e.Property(x => x.ConnectionId).HasMaxLength(64); e.HasIndex(x => new { x.MemberLowId, x.MemberHighId }).IsUnique(); });
        model.Entity<MemberBlock>(e => { e.ToTable("MemberBlock", "social"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedOnAdd(); e.HasIndex(x => new { x.BlockerMemberId, x.BlockedMemberId }); });
        model.Entity<Conversation>(e => { e.ToTable("Conversation", "chat"); e.HasKey(x => x.ConversationId); e.Property(x => x.ConversationId).HasMaxLength(64); e.HasIndex(x => x.ConnectionId).IsUnique(); });
        model.Entity<Message>(e => { e.ToTable("Message", "chat"); e.HasKey(x => x.MessageId); e.Property(x => x.MessageId).HasMaxLength(64); e.Property(x => x.Body).HasMaxLength(4000); e.HasIndex(x => new { x.ConversationId, x.ServerSequence }).IsUnique(); });
        model.Entity<NotificationPreference>(e => { e.ToTable("NotificationPreference", "notification"); e.HasKey(x => new { x.MemberId, x.PurposeCode }); });
        model.Entity<PrivacyRequest>(e => { e.ToTable("PrivacyRequest", "consent"); e.HasKey(x => x.PrivacyRequestId); e.Property(x => x.PrivacyRequestId).HasMaxLength(64); e.HasIndex(x => new { x.MemberId, x.CreatedAt }); });
        model.Entity<SyncChange>(e => { e.ToTable("SyncChange", "ops"); e.HasKey(x => x.SyncSequence); e.Property(x => x.SyncSequence).ValueGeneratedOnAdd(); e.HasIndex(x => new { x.MemberScopeId, x.SyncSequence }); });
        model.Entity<OutboxEvent>(e => { e.ToTable("OutboxEvent", "ops"); e.HasKey(x => x.OutboxEventId); e.Property(x => x.OutboxEventId).HasMaxLength(64); e.HasIndex(x => new { x.PublishedAt, x.OccurredAt }); });
    }
}

public static class LocalDevelopmentSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
        if (db.MemberProfiles.Any()) return;
        db.MemberProfiles.AddRange(
            new MemberProfile { MemberId = "A123", DisplayName = "Asha Rao", Headline = "Pharmaceutical founder", Organization = "Aster Health", Sector = "pharmaceutical", Geography = "Selangor" },
            new MemberProfile { MemberId = "B456", DisplayName = "Ben Lim", Headline = "Cold-chain operator", Organization = "Polar Logistics", Sector = "logistics", Geography = "Selangor" },
            new MemberProfile { MemberId = "D111", DisplayName = "Dana Lee", Headline = "Distribution advisor", Organization = "DL Advisory", Sector = "distribution", Geography = "Kuala Lumpur" });
        db.EventRecords.Add(new EventRecord { EventId = "event-001", Name = "OLGA Connect Pilot", StartsAt = DateTimeOffset.UtcNow.AddDays(-1), EndsAt = DateTimeOffset.UtcNow.AddDays(30) });
        await db.SaveChangesAsync(ct);
    }
}
