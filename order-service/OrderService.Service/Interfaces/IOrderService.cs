using OrderService.Domain.Models;
using OrderService.Service.DTOs;

namespace OrderService.Service.Interfaces;

public interface IOrderService
{
    Task<string> CreateOrderAsync(CreateOrderRequest request);
    Task<Order?> GetOrderAsync(string orderId);
    Task<bool> UpdateOrderStatusAsync(string orderId, OrderStatus status, FulfillmentDetails? fulfillment = null, string? eventId = null);
}
