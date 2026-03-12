using System.Text.Json;
using Confluent.Kafka;
using FulfillmentService.Worker.Models;
using FulfillmentService.Worker.Services;

namespace FulfillmentService.Worker;

public class FulfillmentWorker : BackgroundService
{
    private readonly ILogger<FulfillmentWorker> _logger;
    private readonly IConfiguration _config;
    private readonly IShippingProvider _shippingProvider;
    private readonly HttpClient _httpClient;
    private IConsumer<string, string>? _consumer;

    public FulfillmentWorker(
        ILogger<FulfillmentWorker> logger,
        IConfiguration config,
        IShippingProvider shippingProvider,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _config = config;
        _shippingProvider = shippingProvider;
        _httpClient = httpClientFactory.CreateClient("OrderService");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Fulfillment Worker starting at: {time}", DateTimeOffset.Now);

        var config = new ConsumerConfig
        {
            BootstrapServers = _config["Kafka:BootstrapServers"] ?? "localhost:9092",
            GroupId = _config["Kafka:GroupId"] ?? "fulfillment-service-group",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        _consumer = new ConsumerBuilder<string, string>(config).Build();
        var topic = _config["Kafka:Topic"] ?? "order-events";
        _consumer.Subscribe(topic);

        _logger.LogInformation("Subscribed to topic: {Topic}", topic);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var consumeResult = _consumer.Consume(TimeSpan.FromSeconds(1));
                    
                    if (consumeResult == null)
                        continue;

                    _logger.LogInformation("Received message from Kafka. Key: {Key}, Offset: {Offset}", 
                        consumeResult.Message.Key, consumeResult.Offset);

                    // Extract event-id header if present
                    string? eventId = null;
                    if (consumeResult.Message.Headers != null)
                    {
                        var header = consumeResult.Message.Headers.GetLastBytes("event-id");
                        if (header != null)
                        {
                            eventId = System.Text.Encoding.UTF8.GetString(header);
                        }
                    }

                    await ProcessOrderEventAsync(consumeResult.Message.Value, eventId, stoppingToken);

                    // Commit offset after successful processing
                    _consumer.Commit(consumeResult);
                    _logger.LogInformation("Committed offset: {Offset}", consumeResult.Offset);
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex, "Error consuming from Kafka");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing message");
                    // Don't commit offset on error - message will be retried
                }
            }
        }
        finally
        {
            _consumer?.Close();
            _consumer?.Dispose();
        }
    }

    private async Task ProcessOrderEventAsync(string messageValue, string? eventId, CancellationToken cancellationToken)
    {
        var orderEvent = JsonSerializer.Deserialize<OrderCreatedEvent>(messageValue);
        
        if (orderEvent == null)
        {
            _logger.LogWarning("Failed to deserialize order event");
            return;
        }

        _logger.LogInformation("Processing order {OrderId}, CorrelationId: {CorrelationId}", 
            orderEvent.OrderId, orderEvent.Metadata.CorrelationId);

        // Update order status to Processing
        await UpdateOrderStatusAsync(orderEvent.OrderId, "Processing", orderEvent.Metadata.CorrelationId, null, eventId);

        // Process shipment
        var shipmentResult = await _shippingProvider.ProcessShipmentAsync(
            orderEvent.OrderId, 
            orderEvent.Metadata.CorrelationId);

        // Update order status based on shipment result
        if (shipmentResult.Success)
        {
            await UpdateOrderStatusAsync(
                orderEvent.OrderId, 
                "Shipped", 
                orderEvent.Metadata.CorrelationId,
                new
                {
                    TrackingNumber = shipmentResult.TrackingNumber,
                    Carrier = shipmentResult.Carrier,
                    ShippedAt = shipmentResult.ShippedAt
                },
                eventId);
        }
        else
        {
            await UpdateOrderStatusAsync(
                orderEvent.OrderId, 
                "Failed", 
                orderEvent.Metadata.CorrelationId,
                new
                {
                    ErrorMessage = shipmentResult.ErrorMessage
                },
                eventId);
        }
    }

    private async Task UpdateOrderStatusAsync(string orderId, string status, string correlationId, object? additionalData, string? eventId)
    {
        try
        {
            var payload = new Dictionary<string, object?> { ["Status"] = status };
            
            if (additionalData != null)
            {
                var additionalProps = additionalData.GetType().GetProperties();
                foreach (var prop in additionalProps)
                {
                    payload[prop.Name] = prop.GetValue(additionalData);
                }
            }

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            _logger.LogInformation("Updating order {OrderId} to status {Status}, CorrelationId: {CorrelationId}", 
                orderId, status, correlationId);

            // The Order Service exposes endpoints under /api/v1/orders (API versioning).
            // Ensure the worker calls the versioned route so the request is routed correctly.
            var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/orders/{orderId}/status")
            {
                Content = content
            };

            if (!string.IsNullOrWhiteSpace(eventId))
            {
                request.Headers.Add("X-Event-Id", eventId);
            }

            var response = await _httpClient.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Successfully updated order {OrderId} to {Status}", orderId, status);
            }
            else
            {
                _logger.LogError("Failed to update order {OrderId}. Status code: {StatusCode}", 
                    orderId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating order status for {OrderId}", orderId);
            throw;
        }
    }

    public override void Dispose()
    {
        _consumer?.Dispose();
        base.Dispose();
    }
}
