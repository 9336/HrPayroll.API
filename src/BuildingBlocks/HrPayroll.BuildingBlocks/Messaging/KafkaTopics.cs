using Confluent.Kafka;
using Confluent.Kafka.Admin;
using HrPayroll.Contracts.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HrPayroll.BuildingBlocks.Messaging;

/// <summary>
/// Creates the HR topics (and their dead-letter topics) the first time any service starts, so producers and consumers
/// never race Kafka's auto-creation. Directory topics are log-compacted: the newest message per key is kept forever,
/// which lets a new service build its copy of the directory by reading the topic from the beginning.
/// </summary>
public sealed class KafkaTopics(IOptions<KafkaOptions> options, ILogger<KafkaTopics> logger)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _ready;

    /// <summary>Waits until Kafka answers and the topics exist (retries every 5 seconds).</summary>
    public async Task EnsureCreatedAsync(CancellationToken ct)
    {
        if (_ready) return;
        await _lock.WaitAsync(ct);
        try
        {
            var attempt = 0;
            while (!_ready)
            {
                try
                {
                    await CreateAsync();
                    _ready = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (attempt++ % 6 == 0)
                        logger.LogWarning("Kafka at {Servers} is not reachable yet ({Error}) - retrying", options.Value.BootstrapServers, ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task CreateAsync()
    {
        var o = options.Value;
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = o.BootstrapServers, SocketTimeoutMs = 10000 }).Build();
        var existing = admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Select(t => t.Topic).ToHashSet();

        var wanted = Topics.All.SelectMany(t => new[] { t, Topics.DeadLetter(t) }).Where(t => !existing.Contains(t)).ToList();
        if (wanted.Count == 0) return;

        var specs = wanted.Select(t => new TopicSpecification
        {
            Name = t,
            NumPartitions = t.EndsWith(".dlq") ? 1 : o.Partitions,
            ReplicationFactor = o.ReplicationFactor,
            Configs = Topics.Compacted.Contains(t)
                ? new Dictionary<string, string> { ["cleanup.policy"] = "compact", ["min.compaction.lag.ms"] = "3600000" }
                : new Dictionary<string, string> { ["retention.ms"] = (14L * 24 * 3600 * 1000).ToString() },
        }).ToList();

        try
        {
            await admin.CreateTopicsAsync(specs);
            logger.LogInformation("Created Kafka topics: {Topics}", string.Join(", ", wanted));
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r => r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
            // another service created them at the same moment
        }
    }

    /// <summary>True when the topic has never had a message (used to publish a first full copy of the directory).</summary>
    public bool IsEmpty(string topic)
    {
        var o = options.Value;
        using var consumer = new ConsumerBuilder<Ignore, Ignore>(new ConsumerConfig
        {
            BootstrapServers = o.BootstrapServers,
            GroupId = "hr-topic-inspector",
            EnableAutoCommit = false,
        }).Build();
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = o.BootstrapServers }).Build();
        var meta = admin.GetMetadata(topic, TimeSpan.FromSeconds(10));
        return meta.Topics.Single().Partitions.All(p =>
            consumer.QueryWatermarkOffsets(new TopicPartition(topic, p.PartitionId), TimeSpan.FromSeconds(10)).High.Value == 0);
    }
}
