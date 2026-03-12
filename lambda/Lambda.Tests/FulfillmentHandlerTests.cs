using System.Net;
using System.Text.Json;
using Amazon.Lambda.SQSEvents;
using FluentAssertions;
using FulfillmentService.Lambda;
using FulfillmentService.Lambda.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using OrderProcessingSystemPOC.Shared.LambdaMigration.Shared.Events;
using OrderProcessingSystemPOC.Shared.LambdaMigration.Shared.Idempotency;
using OrderProcessingSystemPOC.Shared.LambdaMigration.Shared.Models;

namespace Lambda.Tests;

public class FulfillmentHandlerTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (FulfillmentHandler handler,
                    Mock<IShippingProvider> shipping,
                    InMemoryIdempotencyStore idempotency,
                    Mock<HttpMessageHandler> httpHandler)
        BuildHandler(HttpStatusCode patchStatus = HttpStatusCode.OK)
    {
        var shipping    = new Mock<IShippingProvider>();
        var idempotency = new InMemoryIdempotencyStore();
        var httpHandler = new Mock<HttpMessageHandler>();

        httpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(patchStatus));

        var httpClient  = new HttpClient(httpHandler.Object);
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OrderService:BaseUrl"] = "https://localhost"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IShippingProvider>(shipping.Object);
        services.AddSingleton<IIdempotencyStore>(idempotency);
        services.AddSingleton<IHttpClientFactory>(httpFactory.Object);
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddConsole());

        var handler = new FulfillmentHandler(services.BuildServiceProvider());
        return (handler, shipping, idempotency, httpHandler);
    }

    private static SQSEvent SingleMessageEvent(string eventId, string orderId) => new()
    {
        Records = new List<SQSEvent.SQSMessage>
        {
            new()
            {
                MessageId = eventId,
                Body      = JsonSerializer.Serialize(new OrderCreatedEvent
                {
                    EventId       = eventId,
                    OrderId       = orderId,
                    CorrelationId = "corr-001",
                    Customer      = new CustomerInfo { CustomerId = "c1", CustomerName = "Test" },
                    Items         = new List<OrderItemInfo>
                        { new() { ProductId = "sku-1", Quantity = 1 } },
                    CreatedAt = DateTime.UtcNow
                })
            }
        }
    };

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ValidMessage_ProcessesShipmentAndPatches()
    {
        var (handler, shipping, _, httpHandler) = BuildHandler();
        shipping.Setup(s => s.ProcessShipmentAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ShippingResult
                {
                    Success        = true,
                    TrackingNumber = "TRACK-123",
                    Carrier        = "MockCarrier",
                    ShippedAt      = DateTime.UtcNow
                });

        var sqsEvent = SingleMessageEvent("evt-001", "order-abc");
        var response = await handler.HandleAsync(sqsEvent, null!);

        response.BatchItemFailures.Should().BeEmpty("no failures expected on happy path");

        // Should PATCH twice: Processing + Shipped
        httpHandler.Protected().Verify(
            "SendAsync",
            Times.Exactly(2),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_DuplicateMessage_SkipsProcessing()
    {
        var (handler, shipping, idempotency, _) = BuildHandler();
        shipping.Setup(s => s.ProcessShipmentAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ShippingResult { Success = true });

        var sqsEvent = SingleMessageEvent("evt-dup", "order-dup");

        // First call processes
        await handler.HandleAsync(sqsEvent, null!);
        // Second call is duplicate — should be skipped
        await handler.HandleAsync(sqsEvent, null!);

        // Shipping should only be called once
        shipping.Verify(
            s => s.ProcessShipmentAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_ShipmentFailure_PatchesStatusFailed()
    {
        var (handler, shipping, _, httpHandler) = BuildHandler();
        shipping.Setup(s => s.ProcessShipmentAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ShippingResult
                {
                    Success      = false,
                    ErrorMessage = "Carrier rejected"
                });

        var sqsEvent = SingleMessageEvent("evt-fail", "order-fail");
        var response = await handler.HandleAsync(sqsEvent, null!);

        response.BatchItemFailures.Should().BeEmpty();

        // Should PATCH twice: Processing + Failed
        httpHandler.Protected().Verify(
            "SendAsync",
            Times.Exactly(2),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ShippingThrows_ReturnsMessageAsBatchFailure()
    {
        var (handler, shipping, _, _) = BuildHandler();
        shipping.Setup(s => s.ProcessShipmentAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ThrowsAsync(new Exception("carrier down"));

        var sqsEvent = SingleMessageEvent("evt-throw", "order-throw");
        var response = await handler.HandleAsync(sqsEvent, null!);

        response.BatchItemFailures.Should().HaveCount(1);
        response.BatchItemFailures[0].ItemIdentifier.Should().Be("evt-throw");
    }

    [Fact]
    public async Task HandleAsync_InvalidJson_SkipsMessageWithoutFailure()
    {
        var (handler, shipping, _, _) = BuildHandler();

        var sqsEvent = new SQSEvent
        {
            Records = new List<SQSEvent.SQSMessage>
            {
                new() { MessageId = "bad-msg", Body = "not-valid-json" }
            }
        };

        var response = await handler.HandleAsync(sqsEvent, null!);

        // Bad JSON is logged and skipped — not returned as a batch failure
        response.BatchItemFailures.Should().BeEmpty();
        shipping.Verify(
            s => s.ProcessShipmentAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleAsync_MultipleMixedMessages_ReturnsOnlyFailedOnes()
    {
        var (handler, shipping, _, _) = BuildHandler();

        shipping.SetupSequence(s => s.ProcessShipmentAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new ShippingResult { Success = true })
                .ThrowsAsync(new Exception("second fails"))
                .ReturnsAsync(new ShippingResult { Success = true });

        var sqsEvent = new SQSEvent
        {
            Records = new List<SQSEvent.SQSMessage>
            {
                new() { MessageId = "m1", Body = JsonSerializer.Serialize(new OrderCreatedEvent { EventId = "m1", OrderId = "o1", CorrelationId = "c", Customer = new CustomerInfo { CustomerId = "c", CustomerName = "t" }, Items = new(), CreatedAt = DateTime.UtcNow }) },
                new() { MessageId = "m2", Body = JsonSerializer.Serialize(new OrderCreatedEvent { EventId = "m2", OrderId = "o2", CorrelationId = "c", Customer = new CustomerInfo { CustomerId = "c", CustomerName = "t" }, Items = new(), CreatedAt = DateTime.UtcNow }) },
                new() { MessageId = "m3", Body = JsonSerializer.Serialize(new OrderCreatedEvent { EventId = "m3", OrderId = "o3", CorrelationId = "c", Customer = new CustomerInfo { CustomerId = "c", CustomerName = "t" }, Items = new(), CreatedAt = DateTime.UtcNow }) },
            }
        };

        var response = await handler.HandleAsync(sqsEvent, null!);

        response.BatchItemFailures.Should().HaveCount(1);
        response.BatchItemFailures[0].ItemIdentifier.Should().Be("m2");
    }
}

