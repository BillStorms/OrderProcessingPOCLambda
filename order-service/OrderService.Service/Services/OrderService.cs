using OrderService.Domain.Models;
using OrderService.Service.Interfaces;
using OrderService.Service.Events;
using OrderService.Service.Models;
using OrderService.Service.Validators;
using OrderService.Service.DTOs;
using Microsoft.Extensions.Logging;

namespace OrderService.Service.Services;

public class OrderManagementService : IOrderService
{
    private readonly IOrderRepository _repo;
    private readonly IEventPublisher _publisher;
    private readonly OrderValidator _validator;
    private readonly ILogger<OrderManagementService> _logger;

    public OrderManagementService(
      IOrderRepository repo,
      IEventPublisher publisher,
      ILogger<OrderManagementService> logger)
    {
        _repo = repo;
        _publisher = publisher;
        _validator = new OrderValidator();
        _logger = logger;
    }

    public async Task<string> CreateOrderAsync(CreateOrderRequest request)
    {
        _validator.Validate(request);

        var correlationId = Guid.NewGuid().ToString();

        _logger.LogInformation("Creating order for customer {CustomerId}, CorrelationId: {CorrelationId}",
          request.CustomerId, correlationId);

        var order = new Order
        {
            OrderId = Guid.NewGuid().ToString(),
            CustomerId = request.CustomerId,
            CustomerName = request.CustomerName,
            Items = request.Items.Select(i => new OrderItem
            {
                ProductId = i.ProductId,
                Quantity = i.Quantity
            }).ToList(),
            Status = OrderStatus.Created,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _repo.InsertAsync(order);

        _logger.LogInformation("Order {OrderId} created successfully, CorrelationId: {CorrelationId}",
          order.OrderId, correlationId);

        var evt = new OrderCreatedEvent
        {
            EventId = Guid.NewGuid().ToString(),
            OrderId = order.OrderId,
            Customer = new CustomerInfo
            {
                CustomerId = order.CustomerId,
                Name = order.CustomerName
            },
            Items = order.Items,
            CreatedAt = order.CreatedAt,
            Metadata = new EventMetadata
            {
                CorrelationId = correlationId
            }
        };

        await _publisher.PublishOrderCreatedAsync(evt);

        _logger.LogInformation("Published OrderCreatedEvent for Order {OrderId}, CorrelationId: {CorrelationId}",
          order.OrderId, correlationId);

        return order.OrderId;
    }

    public Task<Order?> GetOrderAsync(string orderId)
    {
        _logger.LogInformation("Retrieving order {OrderId}", orderId);
        return _repo.GetAsync(orderId);
    }

    public async Task<bool> UpdateOrderStatusAsync(string orderId, OrderStatus status, FulfillmentDetails? fulfillment = null, string? eventId = null)
    {
        _logger.LogInformation("Updating order {OrderId} to status {Status}", orderId, status);

        // If an eventId is provided, attempt to mark it as processed to achieve deduplication
        if (!string.IsNullOrWhiteSpace(eventId))
        {
            var marked = await _repo.TryMarkEventProcessedAsync(eventId);
            if (!marked)
            {
                _logger.LogInformation("Event {EventId} already processed - skipping update for Order {OrderId}", eventId, orderId);
                return true; // Idempotent: event already applied
            }
        }

        var order = await _repo.GetAsync(orderId);
        if (order == null)
        {
            _logger.LogWarning("Order {OrderId} not found for status update", orderId);
            return false;
        }

        // Idempotency check: if status and fulfillment details are unchanged, treat as success and skip DB write
        if (order.Status == status && FulfillmentEquals(order.Fulfillment, fulfillment))
        {
            _logger.LogInformation("Idempotent update detected for Order {OrderId} (status {Status}) - skipping DB write", orderId, status);
            return true;
        }

        order.Status = status;
        order.UpdatedAt = DateTime.UtcNow;

        if (fulfillment != null)
        {
            order.Fulfillment = fulfillment;
        }

        await _repo.UpdateAsync(order);

        _logger.LogInformation("Order {OrderId} updated to status {Status}", orderId, status);
        return true;
    }

    private bool FulfillmentEquals(FulfillmentDetails? a, FulfillmentDetails? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;

        // Compare relevant fields that determine uniqueness of a fulfillment update
        return string.Equals(a.TrackingNumber, b.TrackingNumber, StringComparison.Ordinal) &&
               string.Equals(a.Carrier, b.Carrier, StringComparison.Ordinal) &&
               Nullable.Equals(a.ShippedAt, b.ShippedAt) &&
               string.Equals(a.ErrorMessage, b.ErrorMessage, StringComparison.Ordinal);
    }
}
