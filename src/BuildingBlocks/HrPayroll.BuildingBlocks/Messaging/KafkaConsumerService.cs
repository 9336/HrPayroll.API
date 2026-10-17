using System.Text;
using Confluent.Kafka;
using HrPayroll.BuildingBlocks.Data;
using HrPayroll.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HrPayroll.BuildingBlocks.Messaging;

/// <summary>
/// Reads the topics this service subscribed to (consumer group = the service) and runs the handlers for each message.
/// The handlers' changes and the inbox entry are saved in one transaction, so a message delivered twice is applied once.
/// A message that keeps failing is moved to the topic's dead-letter topic after 5 attempts, so it cannot block the rest.
/// </summary>
public sealed class KafkaConsumerService(
    IServiceScopeFactory scopes, EventSubscriptions subscriptions, KafkaTopics topics, IOptions<KafkaOptions> options,
    IHostEnvironment env, ILogger<KafkaConsumerService> logger) : BackgroundService
{
    private const int MaxAttempts = 5;

    private string GroupId => env.ApplicationName;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => RunAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task RunAsync(CancellationToken ct)
    {
        if (!options.Value.Enabled || subscriptions.Items.Count == 0) return;
        try { await topics.EnsureCreatedAsync(ct); }
        catch (OperationCanceledException) { return; }

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            GroupId = GroupId,
            ClientId = GroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,   // a new service reads the whole history (e.g. builds its directory copy)
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,                // offsets are stored only after a message is processed
            AutoCommitIntervalMs = 1000,
        })
        .SetErrorHandler((_, e) => { if (e.IsFatal) logger.LogError("Kafka consumer error: {Reason}", e.Reason); })
        .Build();
        using var deadLetters = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = options.Value.BootstrapServers }).Build();

        consumer.Subscribe(subscriptions.Topics);
        logger.LogInformation("Listening to {Topics} as {Group}", string.Join(", ", subscriptions.Topics), GroupId);

        while (!ct.IsCancellationRequested)
        {
            ConsumeResult<string, string>? result;
            try
            {
                result = consumer.Consume(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                logger.LogWarning("Kafka consume error: {Reason}", ex.Error.Reason);
                await Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }
            if (result?.Message is null) continue;

            await ProcessWithRetryAsync(result, deadLetters, ct);
            if (ct.IsCancellationRequested) break;
            consumer.StoreOffset(result);
        }

        try { consumer.Close(); } catch { /* shutting down */ }
    }

    private async Task ProcessWithRetryAsync(ConsumeResult<string, string> r, IProducer<string, string> deadLetters, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ProcessAsync(r, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                logger.LogWarning(ex, "Handling {Topic}@{Offset} failed (attempt {Attempt}) - retrying", r.Topic, r.Offset.Value, attempt);
                await Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Handling {Topic}@{Offset} failed {Attempts} times - moved to {DeadLetter}",
                    r.Topic, r.Offset.Value, MaxAttempts, Topics.DeadLetter(r.Topic));
                var headers = r.Message.Headers ?? [];
                headers.Add(Headers.Error, Encoding.UTF8.GetBytes(ex.Message.Length > 500 ? ex.Message[..500] : ex.Message));
                headers.Add("consumer", Encoding.UTF8.GetBytes(GroupId));
                try
                {
                    await deadLetters.ProduceAsync(Topics.DeadLetter(r.Topic),
                        new Message<string, string> { Key = r.Message.Key, Value = r.Message.Value, Headers = headers }, ct);
                }
                catch (Exception dlq) when (dlq is not OperationCanceledException)
                {
                    logger.LogError(dlq, "Could not write to the dead-letter topic");
                }
                return;
            }
        }
    }

    private async Task ProcessAsync(ConsumeResult<string, string> r, CancellationToken ct)
    {
        var type = Header(r, Headers.EventType);
        if (type is null) return;
        var handlers = subscriptions.For(r.Topic, type).ToList();
        if (handlers.Count == 0) return;   // an event this service does not care about

        var messageId = Guid.TryParse(Header(r, Headers.MessageId), out var id) ? id : DeterministicId(r);
        var occurred = DateTime.TryParse(Header(r, Headers.OccurredAt), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            ? at : r.Message.Timestamp.UtcDateTime;
        var context = new EventContext(messageId, r.Topic, r.Message.Key, Header(r, Headers.Source), occurred);

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ServiceDbContext>();
        if (await db.InboxMessages.AnyAsync(m => m.MessageId == messageId && m.Consumer == GroupId, ct)) return;

        foreach (var h in handlers) await h.InvokeAsync(scope.ServiceProvider, r.Message.Value, context, ct);
        db.InboxMessages.Add(new InboxMessage { MessageId = messageId, Consumer = GroupId, ProcessedAt = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    private static string? Header(ConsumeResult<string, string> r, string name) =>
        r.Message.Headers?.TryGetLastBytes(name, out var bytes) == true ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>For messages without an id header: the same message always gets the same id.</summary>
    private static Guid DeterministicId(ConsumeResult<string, string> r)
    {
        var hash = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes($"{r.Topic}/{r.Partition.Value}/{r.Offset.Value}"));
        return new Guid(hash);
    }

    private static async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }
}
