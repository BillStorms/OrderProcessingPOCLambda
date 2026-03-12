using OrderService.Domain.Models;

namespace OrderService.Api.DTOs;

public class UpdateOrderStatusRequestDto
{
    /// <summary>
    /// New order status. Allowed values: Created, Pending, Processing, Shipped, Failed.
    /// </summary>
    public OrderStatus Status { get; set; }

    /// <summary>
    /// Carrier-provided tracking number (typically set when status is Shipped).
    /// </summary>
    public string? TrackingNumber { get; set; }

    /// <summary>
    /// Carrier name (for example: MockCarrier, UPS, FedEx).
    /// </summary>
    public string? Carrier { get; set; }

    /// <summary>
    /// Shipment timestamp in UTC.
    /// </summary>
    public DateTime? ShippedAt { get; set; }

    /// <summary>
    /// Error details when status is Failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
}
