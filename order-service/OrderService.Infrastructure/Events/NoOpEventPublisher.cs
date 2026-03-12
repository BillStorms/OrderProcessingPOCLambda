using Microsoft.Extensions.Logging;
using OrderService.Service.Events;
using OrderService.Service.Interfaces;

namespace OrderService.Infrastructure.Events;

/// <summary>
/// Local-development / standalone event publisher — logs the event instead of sending
/// it to an external broker. In production the Lambda uses SqsEventPublisher.
/// </summary>
public sealed class NoOpEventPublisher : IEventPublisher
{
    private readonly ILogger<NoOpEventPublisher> _logger;

    public NoOpEventPublisher(ILogger<NoOpEventPublisher> logger)
    {
        _logger = logger;
    }

    public Task PublishOrderCreatedAsync(OrderCreatedEvent evt)
    {
        _logger.LogInformation(
            "[NoOpEventPublisher] OrderCreatedEvent would be published. OrderId={OrderId}, EventId={EventId}",
            evt.OrderId, evt.EventId);

        return Task.CompletedTask;
    }
}

