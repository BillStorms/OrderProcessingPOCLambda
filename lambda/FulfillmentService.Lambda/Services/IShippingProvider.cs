namespace FulfillmentService.Lambda.Services;

public interface IShippingProvider
{
    Task<ShippingResult> ProcessShipmentAsync(string orderId, string correlationId);
}

