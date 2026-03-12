using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.DynamoDBv2;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FulfillmentService.Lambda.Services;
using OrderProcessingSystemPOC.Shared.LambdaMigration.Shared.Events;
using OrderProcessingSystemPOC.Shared.LambdaMigration.Shared.Idempotency;

// Tell Lambda which JSON serializer to use
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace FulfillmentService.Lambda;

/// <summary>
/// SQS-triggered Lambda function that replaces the FulfillmentWorker background service.
/// Each SQS message carries a serialized OrderCreatedEvent.
/// Idempotency is enforced via DynamoDB before any processing occurs.
/// </summary>
public class FulfillmentHandler
{
    private readonly IShippingProvider    _shipping;
    private readonly IIdempotencyStore    _idempotency;
    private readonly IHttpClientFactory   _httpFactory;
    private readonly ILogger<FulfillmentHandler> _logger;
    private readonly string               _orderServiceBaseUrl;

    /// <summary>
    /// Parameterless constructor used by the Lambda runtime.
    /// Bootstraps DI manually (no Host, to keep cold-start lean).
    /// </summary>
    public FulfillmentHandler() : this(BuildServiceProvider()) { }

    /// <summary>Constructor used for unit testing (inject mocks).</summary>
    public FulfillmentHandler(IServiceProvider services)
    {
        _shipping    = services.GetRequiredService<IShippingProvider>();
        _idempotency = services.GetRequiredService<IIdempotencyStore>();
        _httpFactory = services.GetRequiredService<IHttpClientFactory>();
        _logger      = services.GetRequiredService<ILogger<FulfillmentHandler>>();

        var config           = services.GetRequiredService<IConfiguration>();
        _orderServiceBaseUrl = config["OrderService:BaseUrl"]
            ?? throw new InvalidOperationException("OrderService:BaseUrl is not configured");
    }

    // ── Lambda entry point ──────────────────────────────────────────────────

    public async Task<SQSBatchResponse> HandleAsync(SQSEvent sqsEvent, ILambdaContext context)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();

        foreach (var record in sqsEvent.Records)
        {
            try
            {
                await ProcessRecordAsync(record);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process SQS record {MessageId}", record.MessageId);
                // Report partial batch failure — only failed messages return to the queue
                failures.Add(new SQSBatchResponse.BatchItemFailure
                {
                    ItemIdentifier = record.MessageId
                });
            }
        }

        return new SQSBatchResponse { BatchItemFailures = failures };
    }

    // ── Per-message processing ──────────────────────────────────────────────

    private async Task ProcessRecordAsync(SQSEvent.SQSMessage record)
    {
        OrderCreatedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<OrderCreatedEvent>(record.Body);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not deserialize SQS message {MessageId} — skipping", record.MessageId);
            return;
        }

        if (evt is null)
        {
            _logger.LogWarning("Could not deserialize SQS message {MessageId} — skipping", record.MessageId);
            return;
        }

        // Use SQS MessageId as fallback eventId if the event didn't carry one
        var eventId = string.IsNullOrWhiteSpace(evt.EventId) ? record.MessageId : evt.EventId;

        // ── Idempotency gate ────────────────────────────────────────────────
        var claimed = await _idempotency.TryClaimAsync(eventId);
        if (!claimed)
        {
            _logger.LogInformation(
                "Duplicate event {EventId} for order {OrderId} — skipping",
                eventId, evt.OrderId);
            return;
        }

        _logger.LogInformation(
            "Processing order {OrderId}, correlationId={CorrelationId}",
            evt.OrderId, evt.CorrelationId);

        // ── Mark order as Processing ────────────────────────────────────────
        await PatchOrderStatusAsync(evt.OrderId, "Processing", null, eventId);

        // ── Run shipment ────────────────────────────────────────────────────
        var result = await _shipping.ProcessShipmentAsync(evt.OrderId, evt.CorrelationId ?? evt.OrderId);

        if (result.Success)
        {
            await PatchOrderStatusAsync(evt.OrderId, "Shipped", new
            {
                result.TrackingNumber,
                result.Carrier,
                result.ShippedAt
            }, eventId);
        }
        else
        {
            await PatchOrderStatusAsync(evt.OrderId, "Failed", new
            {
                result.ErrorMessage
            }, eventId);
        }
    }

    // ── HTTP call back to Order Service ─────────────────────────────────────

    private async Task PatchOrderStatusAsync(string orderId, string status, object? extra, string eventId)
    {
        var payload = new Dictionary<string, object?> { ["Status"] = status };
        if (extra is not null)
        {
            foreach (var p in extra.GetType().GetProperties())
                payload[p.Name] = p.GetValue(extra);
        }

        var body    = new StringContent(JsonSerializer.Serialize(payload),
                          System.Text.Encoding.UTF8, "application/json");
        var client  = _httpFactory.CreateClient("OrderService");
        var request = new HttpRequestMessage(HttpMethod.Patch,
                          $"{_orderServiceBaseUrl}/api/v1/orders/{orderId}/status")
        {
            Content = body
        };
        request.Headers.Add("X-Event-Id", eventId);

        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            _logger.LogError("PATCH /orders/{OrderId}/status returned {Status}", orderId, response.StatusCode);
    }

    // ── DI bootstrap (used by parameterless constructor) ────────────────────

    private static IServiceProvider BuildServiceProvider()
    {
        var config = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddConsole());
        services.AddHttpClient("OrderService");

        // Idempotency: DynamoDB in production, InMemory locally
        var dynamoTable = config["DynamoDB:IdempotencyTable"];
        if (!string.IsNullOrWhiteSpace(dynamoTable))
        {
            services.AddSingleton<IAmazonDynamoDB>(_ => new Amazon.DynamoDBv2.AmazonDynamoDBClient());
            services.AddSingleton<IIdempotencyStore>(sp =>
                new DynamoDbIdempotencyStore(sp.GetRequiredService<IAmazonDynamoDB>(), dynamoTable));
        }
        else
        {
            services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        }

        services.AddSingleton<IShippingProvider, MockShippingProvider>();

        return services.BuildServiceProvider();
    }
}

