using Microsoft.EntityFrameworkCore;
using Olga.Core.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
var connection = builder.Configuration.GetConnectionString("AzureSql");
if (string.IsNullOrWhiteSpace(connection)) builder.Services.AddDbContext<CoreDbContext>(o => o.UseInMemoryDatabase("olga-core-worker-local"));
else builder.Services.AddDbContext<CoreDbContext>(o => o.UseSqlServer(connection));
builder.Services.AddHostedService<OutboxPublisherWorker>();
await builder.Build().RunAsync();

public sealed class OutboxPublisherWorker(IServiceScopeFactory scopes, ILogger<OutboxPublisherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
            var pending = await db.OutboxEvents.Where(x => x.PublishedAt == null).OrderBy(x => x.OccurredAt).Take(50).ToListAsync(stoppingToken);
            foreach (var item in pending) { logger.LogInformation("Outbox event {EventId} {EventType} is ready for transport", item.OutboxEventId, item.EventType); item.AttemptCount++; }
            await db.SaveChangesAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
