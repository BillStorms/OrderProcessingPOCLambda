using OrderService.Domain.Models;

namespace OrderService.Service.Interfaces;

public interface IOrderRepository
{
    Task InsertAsync(Order order);
    Task<Order?> GetAsync(string orderId);
    Task UpdateAsync(Order order);

    // Attempts to mark an event as processed. Returns true if successfully marked (not seen before), false if event already exists.
    Task<bool> TryMarkEventProcessedAsync(string eventId);
}
