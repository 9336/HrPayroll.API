using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HrPayroll.BuildingBlocks.Messaging;

/// <summary>One handler for one event type on one topic.</summary>
public sealed record EventSubscription(string Topic, Type EventType, Type HandlerType)
{
    public string EventTypeName => EventType.Name;

    /// <summary>Deserializes the payload and calls the handler resolved from <paramref name="services"/>.</summary>
    internal Task InvokeAsync(IServiceProvider services, string payload, EventContext context, CancellationToken ct)
    {
        var evt = JsonSerializer.Deserialize(payload, EventType, EventJson.Options)
                  ?? throw new InvalidOperationException($"Empty {EventTypeName} payload.");
        var handler = services.GetRequiredService(HandlerType);
        var method = typeof(IIntegrationEventHandler<>).MakeGenericType(EventType).GetMethod(nameof(IIntegrationEventHandler<object>.HandleAsync))!;
        return (Task)method.Invoke(handler, [evt, context, ct])!;
    }
}

/// <summary>Everything this service listens to (filled by <see cref="EventSubscriptionExtensions.AddEventHandler{TEvent,THandler}"/>).</summary>
public sealed class EventSubscriptions
{
    private readonly List<EventSubscription> _items = [];

    public IReadOnlyList<EventSubscription> Items => _items;

    public IReadOnlyCollection<string> Topics => _items.Select(s => s.Topic).Distinct().ToList();

    internal void Add(EventSubscription s)
    {
        if (!_items.Contains(s)) _items.Add(s);
    }

    public IEnumerable<EventSubscription> For(string topic, string eventType) =>
        _items.Where(s => s.Topic == topic && s.EventTypeName == eventType);
}

public static class EventSubscriptionExtensions
{
    /// <summary>Subscribes <typeparamref name="THandler"/> to <typeparamref name="TEvent"/> messages on <paramref name="topic"/>.</summary>
    public static IServiceCollection AddEventHandler<TEvent, THandler>(this IServiceCollection services, string topic)
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        services.TryAddScoped<THandler>();
        Subscriptions(services).Add(new EventSubscription(topic, typeof(TEvent), typeof(THandler)));
        return services;
    }

    internal static EventSubscriptions Subscriptions(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(EventSubscriptions))?.ImplementationInstance as EventSubscriptions;
        if (existing is not null) return existing;
        var created = new EventSubscriptions();
        services.AddSingleton(created);
        return created;
    }
}
