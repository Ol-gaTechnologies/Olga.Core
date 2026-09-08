using System.Data;
using Microsoft.EntityFrameworkCore;
using Olga.Core.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
var connection = builder.Configuration.GetConnectionString("PostgreSql");
if (string.IsNullOrWhiteSpace(connection)) builder.Services.AddDbContextPool<CoreDbContext>(o => o.UseInMemoryDatabase("olga-core-worker-local"));
else builder.Services.AddDbContextPool<CoreDbContext>(o => o.UseOlgaPostgreSql(connection, enableRetryOnFailure: false));
builder.Services.AddHostedService<OutboxPublisherWorker>();
await builder.Build().RunAsync();

public sealed class OutboxPublisherWorker(IServiceScopeFactory scopes, ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
                if (db.Database.IsNpgsql()) await ProcessPostgreSqlBatchAsync(db, stoppingToken);
                else await ProcessLocalBatchAsync(db, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Outbox batch failed; the next polling cycle will retry it"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task ProcessPostgreSqlBatchAsync(CoreDbContext db, CancellationToken ct)
    {
        // READ COMMITTED + SKIP LOCKED permits competing workers without duplicate leases or convoying.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var rows = await db.OutboxEvents
            .FromSqlRaw("SELECT * FROM ops.outbox_event WHERE published_at IS NULL ORDER BY occurred_at, outbox_event_id FOR UPDATE SKIP LOCKED LIMIT 50")
            .ToListAsync(ct);
        foreach (var row in rows) { logger.LogInformation("Outbox event {EventId} {EventType} is ready for transport", row.OutboxEventId, row.EventType); row.AttemptCount++; }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task ProcessLocalBatchAsync(CoreDbContext db, CancellationToken ct)
    {
        var rows = await db.OutboxEvents.Where(x => x.PublishedAt == null).OrderBy(x => x.OccurredAt).ThenBy(x => x.OutboxEventId).Take(50).ToListAsync(ct);
        foreach (var row in rows) { logger.LogInformation("Outbox event {EventId} {EventType} is ready for transport", row.OutboxEventId, row.EventType); row.AttemptCount++; }
        await db.SaveChangesAsync(ct);
    }
}
