using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using Moq;
using OrderService.Domain.Models;
using OrderService.Infrastructure.Repositories;

namespace OrderService.Tests;

public class DynamoDbOrderRepositoryTests
{
    [Fact]
    public async Task InsertAsync_ShouldWriteOrderWithConditionalPut()
    {
        var dynamo = new Mock<IAmazonDynamoDB>();
        PutItemRequest? capturedRequest = null;

        dynamo
            .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutItemRequest, CancellationToken>((request, _) => capturedRequest = request)
            .ReturnsAsync(new PutItemResponse());

        var repository = new DynamoDbOrderRepository(dynamo.Object, "orders-dev", "order-idempotency-dev");
        var order = CreateOrder("order-1");

        await repository.InsertAsync(order);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.TableName.Should().Be("orders-dev");
        capturedRequest.ConditionExpression.Should().Be("attribute_not_exists(OrderId)");
        capturedRequest.Item["OrderId"].S.Should().Be("order-1");
        capturedRequest.Item["Payload"].S.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnOrderWhenItemExists()
    {
        var dynamo = new Mock<IAmazonDynamoDB>();
        var seedOrder = CreateOrder("order-2");
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(seedOrder);

        dynamo
            .Setup(d => d.GetItemAsync(It.IsAny<GetItemRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetItemResponse
            {
                Item = new Dictionary<string, AttributeValue>
                {
                    ["OrderId"] = new() { S = "order-2" },
                    ["Payload"] = new() { S = payloadJson }
                }
            });

        var repository = new DynamoDbOrderRepository(dynamo.Object, "orders-dev", "order-idempotency-dev");
        var loaded = await repository.GetAsync("order-2");

        loaded.Should().NotBeNull();
        loaded!.OrderId.Should().Be("order-2");
        loaded.CustomerId.Should().Be(seedOrder.CustomerId);
        loaded.Items.Should().HaveCount(1);
    }

    [Fact]
    public async Task TryMarkEventProcessedAsync_ShouldUseNamespacedKeyAndReturnFalseOnDuplicate()
    {
        var dynamo = new Mock<IAmazonDynamoDB>();
        var requests = new List<PutItemRequest>();
        var callCount = 0;

        dynamo
            .Setup(d => d.PutItemAsync(It.IsAny<PutItemRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutItemRequest, CancellationToken>((request, _) => requests.Add(request))
            .Returns<PutItemRequest, CancellationToken>((_, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return Task.FromResult(new PutItemResponse());
                }

                throw new ConditionalCheckFailedException("duplicate");
            });

        var repository = new DynamoDbOrderRepository(dynamo.Object, "orders-dev", "order-idempotency-dev");

        var first = await repository.TryMarkEventProcessedAsync("evt-123");
        var second = await repository.TryMarkEventProcessedAsync("evt-123");

        first.Should().BeTrue();
        second.Should().BeFalse();

        var firstRequest = requests.First(r => r.TableName == "order-idempotency-dev");
        firstRequest.ConditionExpression.Should().Be("attribute_not_exists(EventId)");
        firstRequest.Item["EventId"].S.Should().Be("ORDER_STATUS#evt-123");
        firstRequest.Item.Should().ContainKey("ExpiresAt");
    }

    private static Order CreateOrder(string orderId)
    {
        return new Order
        {
            OrderId = orderId,
            CustomerId = "cust-1",
            CustomerName = "Jane Doe",
            Status = OrderStatus.Created,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Items =
            [
                new OrderItem
                {
                    ProductId = "sku-1",
                    Quantity = 2
                }
            ]
        };
    }
}


