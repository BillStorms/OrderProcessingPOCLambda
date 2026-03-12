using OrderService.Domain.Models;
using OrderService.Service.Models;

namespace OrderService.Service.Events;

public class OrderCreatedEvent
{
    public string EventType { get; set; } = "OrderCreated";
    public string EventId { get; set; } = null!; // Unique identifier for the event (for idempotency/dedup)
    public string OrderId { get; set; } = null!;
    public CustomerInfo Customer { get; set; } = null!;
    public List<OrderItem> Items { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public EventMetadata Metadata { get; set; } = null!;
}