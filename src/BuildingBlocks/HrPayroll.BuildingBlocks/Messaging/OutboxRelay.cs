using System.Text;
using Confluent.Kafka;
using HrPayroll.BuildingBlocks.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HrPayroll.BuildingBlocks.Messaging;

/// <summary>
/// Publishes the outbox to Kafka in the order the events were saved (one relay per service, idempotent producer),
/// then marks them sent. Sent rows are deleted after three days.
/// </summary>
public sealed class OutboxRelay(
    IServiceScopeFactory scopes, KafkaTopics topics, OutboxSignal signal, IOptions<KafkaOptions> options,
    IHostEnvironment env, ILogger<OutboxRelay> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private DateTime _lastCleanup = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) { logger.LogWarning("Kafka is switched off - events stay in the outbox."); return; }
        await Task.Yield();
        try { await topics.EnsureCreatedAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            ClientId = env.ApplicationName,
            EnableIdempotence = true,   // no duplicates or reordering on producer retries
            Acks = Acks.All,
            MessageTimeoutMs = 15000,
            LingerMs = 5,
        }).Build();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sent = await PublishBatchAsync(producer, stoppingToken);
                if (sent < BatchSize) await signal.WaitAsync(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Publishing the outbox failed");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
        try { producer.Flush(TimeSpan.FromSeconds(5)); } catch { /* shutting down */ }
    }

    private const int BatchSize = 100;

    private async Task<int> PublishBatchAsync(IProducer<string, string> producer, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        var batch = await db.OutboxMessages.Where(m => m.SentAt == null).OrderBy(m => m.Id).Take(BatchSize).ToListAsync(ct);

        var sent = 0;
        foreach (var m in batch)
        {
            try
            {
                await producer.ProduceAsync(m.Topic, new Message<string, string>
                {
                    Key = m.MessageKey,
                    Value = m.Payload,
                    Headers = new Confluent.Kafka.Headers
                    {
                        { Headers.MessageId, Encoding.UTF8.GetBytes(m.MessageId.ToString()) },
                        { Headers.EventType, Encoding.UTF8.GetBytes(m.EventType) },
                        { Headers.Source, Encoding.UTF8.GetBytes(env.ApplicationName) },
                        { Headers.OccurredAt, Encoding.UTF8.GetBytes(m.OccurredAt.ToString("O")) },
                    },
                }, ct);
                m.SentAt = DateTime.UtcNow;
                m.Attempts++;
                m.LastError = null;
                sent++;
            }
            catch (ProduceException<string, string> ex)
            {
                // Stop at the first failure so later events of the same record are not sent before this one.
                m.Attempts++;
                m.LastError = ex.Error.Reason.Length > 1000 ? ex.Error.Reason[..1000] : ex.Error.Reason;
                logger.LogWarning("Could not publish {Type} {Id} to {Topic}: {Error}", m.EventType, m.MessageId, m.Topic, ex.Error.Reason);
                break;
            }
        }
        await db.SaveChangesAsync(ct);

        if (DateTime.UtcNow - _lastCleanup > TimeSpan.FromHours(1))
        {
            _lastCleanup = DateTime.UtcNow;
            var before = DateTime.UtcNow.AddDays(-3);
            await db.OutboxMessages.Where(m => m.SentAt != null && m.SentAt < before).ExecuteDeleteAsync(ct);
            await db.InboxMessages.Where(m => m.ProcessedAt < before.AddDays(-4)).ExecuteDeleteAsync(ct);
        }
        if (sent > 0) logger.LogDebug("Published {Count} event(s)", sent);
        return sent;   // a full batch means there may be more waiting
    }
}
