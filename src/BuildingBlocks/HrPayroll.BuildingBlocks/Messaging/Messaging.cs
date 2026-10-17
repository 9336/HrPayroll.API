using System.Text.Json;
using System.Text.Json.Serialization;

namespace HrPayroll.BuildingBlocks.Messaging;

/// <summary>Kafka connection ("Kafka" section).</summary>
public class KafkaOptions
{
    public const string Section = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";
    /// <summary>Partitions for topics this service creates (existing topics are left as they are).</summary>
    public int Partitions { get; set; } = 3;
    public short ReplicationFactor { get; set; } = 1;
    /// <summary>Off = the service runs without Kafka (events stay in the outbox until it is switched on).</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Transactional outbox: an integration event is saved in the service's own database in the same transaction as the
/// business change, and <see cref="OutboxRelay"/> publishes it to Kafka afterwards. Nothing is lost if Kafka is down,
/// and nothing is published for a change that was rolled back.
/// </summary>
public class OutboxMessage
{
    public long Id { get; set; }
    public Guid MessageId { get; set; }
    public string Topic { get; set; } = default!;
    public string MessageKey { get; set; } = default!;
    public string EventType { get; set; } = default!;
    public string Payload { get; set; } = default!;
    public DateTime OccurredAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Messages a consumer has processed - Kafka delivers at least once, so repeats are skipped.</summary>
public class InboxMessage
{
    public Guid MessageId { get; set; }
    public string Consumer { get; set; } = default!;
    public DateTime ProcessedAt { get; set; }
}

/// <summary>Kafka message headers.</summary>
public static class Headers
{
    public const string MessageId = "message-id";
    public const string EventType = "event-type";
    public const string Source = "source";
    public const string OccurredAt = "occurred-at";
    public const string Error = "error";
}

public static class EventJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Where a consumed event came from.</summary>
public record EventContext(Guid MessageId, string Topic, string Key, string? Source, DateTime OccurredAt);

/// <summary>Handles one kind of integration event. Runs in its own DI scope; changes are saved by the consumer.</summary>
public interface IIntegrationEventHandler<in TEvent>
{
    Task HandleAsync(TEvent evt, EventContext context, CancellationToken ct);
}

/// <summary>Wakes the outbox relay as soon as something is saved, so events go out without waiting for the next poll.</summary>
public sealed class OutboxSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
        }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}
