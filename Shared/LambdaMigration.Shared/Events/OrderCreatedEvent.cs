using OrderProcessingSystemPOC.Shared.LambdaMigration.Shared.Models;

namespace OrderProcessingSystemPOC.Shared.LambdaMigration.Shared.Events;

public class OrderCreatedEvent
{
    public string EventType { get; set; } = "OrderCreated";
    public string EventId   { get; set; } = null!;
    public string OrderId   { get; set; } = null!;
    public CustomerInfo Customer { get; set; } = null!;
    public List<OrderItemInfo> Items { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public string CorrelationId { get; set; } = null!;
}

