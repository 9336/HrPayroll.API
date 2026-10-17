using HrPayroll.BuildingBlocks.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HrPayroll.BuildingBlocks.Messaging;

/// <summary>The current state of every record an owning service publishes to <see cref="Topic"/>.</summary>
public sealed record SnapshotSource(string Topic, Func<ServiceDbContext, CancellationToken, Task<IEnumerable<OutboxEvent>>> Load);

/// <summary>
/// When an owning service starts against an empty topic (a new Kafka cluster), it publishes the current state of all its
/// records once, so services that start later can build their copies by reading the topic from the beginning.
/// </summary>
public sealed class SnapshotPublisher(
    IServiceScopeFactory scopes, KafkaTopics topics, IEnumerable<SnapshotSource> sources, IOptions<KafkaOptions> options,
    ILogger<SnapshotPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        try
        {
            await topics.EnsureCreatedAsync(stoppingToken);
            foreach (var source in sources)
            {
                if (!await IsEmptyAsync(source.Topic, stoppingToken)) continue;
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
                var events = (await source.Load(db, stoppingToken)).ToList();
                foreach (var e in events) db.Publish(e.Topic, e.Key, e.Payload);
                await db.SaveChangesAsync(stoppingToken);
                logger.LogInformation("{Topic} was empty - published the current {Count} record(s)", source.Topic, events.Count);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Publishing the first snapshot failed");
        }
    }

    /// <summary>Topics just created may not have a partition leader for a few seconds: retry.</summary>
    private async Task<bool> IsEmptyAsync(string topic, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return topics.IsEmpty(topic); }
            catch (Confluent.Kafka.KafkaException ex) when (attempt < 20)
            {
                logger.LogDebug("Checking {Topic} failed ({Error}) - retrying", topic, ex.Error.Reason);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }
}

public static class SnapshotExtensions
{
    public static IServiceCollection AddSnapshot(this IServiceCollection services, string topic,
        Func<ServiceDbContext, CancellationToken, Task<IEnumerable<OutboxEvent>>> load)
    {
        services.AddSingleton(new SnapshotSource(topic, load));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SnapshotPublisher>());
        return services;
    }
}
