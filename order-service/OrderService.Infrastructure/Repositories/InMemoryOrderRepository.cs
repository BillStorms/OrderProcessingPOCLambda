using OrderService.Domain.Models;
using OrderService.Service.Interfaces;

namespace OrderService.Infrastructure.Repositories;

public class InMemoryOrderRepository : IOrderRepository
{
    private readonly Dictionary<string, Order> _orders = new();
    private readonly HashSet<string> _processedEvents = new();
    private readonly object _lock = new();

    public Task InsertAsync(Order order)
    {
        _orders[order.OrderId] = order;
        return Task.CompletedTask;
    }

    public Task<Order?> GetAsync(string orderId)
    {
        _orders.TryGetValue(orderId, out var order);
        return Task.FromResult(order);
    }

    public Task UpdateAsync(Order order)
    {
        _orders[order.OrderId] = order;
        return Task.CompletedTask;
    }

    public Task<bool> TryMarkEventProcessedAsync(string eventId)
    {
        lock (_lock)
        {
            if (_processedEvents.Contains(eventId))
                return Task.FromResult(false);

            _processedEvents.Add(eventId);
            return Task.FromResult(true);
        }
    }
}
