using Microsoft.Extensions.Logging;

namespace FulfillmentService.Lambda.Services;

/// <summary>
/// Mock shipping provider — mirrors the existing MockShippingProvider from the worker project.
/// Swap this for a real carrier SDK (UPS, FedEx, etc.) without touching the Lambda handler.
/// </summary>
public sealed class MockShippingProvider : IShippingProvider
{
    private readonly ILogger<MockShippingProvider> _logger;

    public MockShippingProvider(ILogger<MockShippingProvider> logger) => _logger = logger;

    public Task<ShippingResult> ProcessShipmentAsync(string orderId, string correlationId)
    {
        _logger.LogInformation("MockShippingProvider: processing shipment for order {OrderId}", orderId);

        // Simulate ~10 % failure rate for testing idempotency / retry paths
        var fail = orderId.GetHashCode() % 10 == 0;
        if (fail)
        {
            return Task.FromResult(new ShippingResult
            {
                Success      = false,
                ErrorMessage = "Simulated carrier rejection"
            });
        }

        return Task.FromResult(new ShippingResult
        {
            Success        = true,
            TrackingNumber = $"TRACK-{orderId[..Math.Min(8, orderId.Length)].ToUpper()}",
            Carrier        = "MockCarrier",
            ShippedAt      = DateTime.UtcNow
        });
    }
}

