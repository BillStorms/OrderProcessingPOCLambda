namespace OrderService.Domain.Models;

public class ProcessedEvent
{
    public string EventId { get; set; } = null!;
    public DateTime ProcessedAt { get; set; }
}

