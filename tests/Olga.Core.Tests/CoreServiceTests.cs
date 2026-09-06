using Microsoft.EntityFrameworkCore;
using Olga.Core.Application;
using Olga.Core.Contracts;
using Olga.Core.Domain;
using Olga.Core.Infrastructure;

namespace Olga.Core.Tests;

public sealed class CoreServiceTests
{
    [Fact]
    public async Task Profile_update_requires_matching_etag()
    {
        await using var db = Db();
        db.MemberProfiles.Add(new MemberProfile { MemberId = "A", DisplayName = "A" }); await db.SaveChangesAsync();
        var service = Service(db);
        await Assert.ThrowsAsync<DomainException>(() => service.UpdateProfileAsync("A", new("New", null, null, null, null, null), null, default));
        var updated = await service.UpdateProfileAsync("A", new("New", null, null, null, null, null), "\"1\"", default);
        Assert.Equal("\"2\"", updated.ETag);
    }

    [Fact]
    public async Task Live_mode_requires_registration_and_latest_consent()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        var service = Service(db);
        await Assert.ThrowsAsync<DomainException>(() => service.StartLiveModeAsync("A", "E", new(), default));
        await service.RegisterAsync("A", "E", default);
        await service.RecordConsentAsync("A", new("LIVE_MODE", "1", "GRANTED"), default);
        var session = await service.StartLiveModeAsync("A", "E", new(30), default);
        Assert.Equal("ACTIVE", session.Status);
        await service.RecordConsentAsync("A", new("LIVE_MODE", "1", "WITHDRAWN"), default);
        Assert.Equal("REVOKED", db.LiveModeSessions.Single().Status);
    }

    [Fact]
    public async Task Accepting_request_creates_one_connection_and_conversation()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        var service = Service(db);
        var request = await service.CreateConnectionRequestAsync("A", new("B"), default);
        var accepted = await service.DecideConnectionRequestAsync("B", request.RequestId, new("ACCEPT"), default);
        Assert.Single(db.SocialConnections); Assert.Single(db.ChatConversations); Assert.NotEmpty(accepted.ConversationId);
        var message = await service.SendMessageAsync("A", accepted.ConversationId, new("m1", "Hello"), default);
        var replay = await service.SendMessageAsync("A", accepted.ConversationId, new("m1", "Hello"), default);
        Assert.Equal(message.ServerSequence, replay.ServerSequence); Assert.Single(db.ChatMessages);
    }

    [Fact]
    public async Task Block_immediately_prevents_relationship_and_chat()
    {
        await using var db = Db(); SeedMembersAndEvent(db); await db.SaveChangesAsync();
        var service = Service(db);
        var request = await service.CreateConnectionRequestAsync("A", new("B"), default);
        var accepted = await service.DecideConnectionRequestAsync("B", request.RequestId, new("ACCEPT"), default);
        await service.BlockAsync("A", new("B"), default);
        var projection = await service.GetNlpRelationshipAsync("A", "B", default);
        Assert.True(projection.Blocked); Assert.False(projection.Connected);
        await Assert.ThrowsAsync<DomainException>(() => service.SendMessageAsync("B", accepted.ConversationId, new("m2", "No"), default));
    }

    private static CoreDbContext Db() => new(new DbContextOptionsBuilder<CoreDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static CoreService Service(CoreDbContext db) => new(db);
    private static void SeedMembersAndEvent(CoreDbContext db)
    {
        db.MemberProfiles.AddRange(new MemberProfile { MemberId = "A", DisplayName = "A" }, new MemberProfile { MemberId = "B", DisplayName = "B" });
        db.EventRecords.Add(new EventRecord { EventId = "E", Name = "Event", StartsAt = DateTimeOffset.UtcNow.AddHours(-1), EndsAt = DateTimeOffset.UtcNow.AddDays(1) });
    }
}
