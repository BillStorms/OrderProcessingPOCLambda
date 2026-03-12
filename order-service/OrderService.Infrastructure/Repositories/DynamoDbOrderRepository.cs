using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using OrderService.Domain.Models;
using OrderService.Service.Interfaces;

namespace OrderService.Infrastructure.Repositories;

public sealed class DynamoDbOrderRepository : IOrderRepository
{
    private const string OrderIdAttribute = "OrderId";
    private const string PayloadAttribute = "Payload";
    private const string EventIdAttribute = "EventId";

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _ordersTableName;
    private readonly string? _processedEventsTableName;

    public DynamoDbOrderRepository(
        IAmazonDynamoDB dynamoDb,
        string ordersTableName,
        string? processedEventsTableName = null)
    {
        _dynamoDb = dynamoDb;
        _ordersTableName = ordersTableName;
        _processedEventsTableName = processedEventsTableName;
    }

    public async Task InsertAsync(Order order)
    {
        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _ordersTableName,
            Item = ToOrderItem(order),
            ConditionExpression = "attribute_not_exists(OrderId)"
        });
    }

    public async Task<Order?> GetAsync(string orderId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _ordersTableName,
            Key = new Dictionary<string, AttributeValue>
            {
                [OrderIdAttribute] = new AttributeValue { S = orderId }
            },
            ConsistentRead = true
        });

        if (response.Item == null || response.Item.Count == 0)
        {
            return null;
        }

        return FromOrderItem(response.Item);
    }

    public async Task UpdateAsync(Order order)
    {
        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _ordersTableName,
            Item = ToOrderItem(order),
            ConditionExpression = "attribute_exists(OrderId)"
        });
    }

    public async Task<bool> TryMarkEventProcessedAsync(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(_processedEventsTableName))
        {
            return true;
        }

        var normalizedEventId = $"ORDER_STATUS#{eventId}";
        var expiresAt = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();

        try
        {
            await _dynamoDb.PutItemAsync(new PutItemRequest
            {
                TableName = _processedEventsTableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    [EventIdAttribute] = new AttributeValue { S = normalizedEventId },
                    ["ClaimedAt"] = new AttributeValue { S = DateTime.UtcNow.ToString("O") },
                    ["ExpiresAt"] = new AttributeValue { N = expiresAt.ToString() }
                },
                ConditionExpression = "attribute_not_exists(EventId)"
            });

            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    private static Dictionary<string, AttributeValue> ToOrderItem(Order order)
    {
        return new Dictionary<string, AttributeValue>
        {
            [OrderIdAttribute] = new AttributeValue { S = order.OrderId },
            [PayloadAttribute] = new AttributeValue { S = JsonSerializer.Serialize(order) }
        };
    }

    private static Order FromOrderItem(Dictionary<string, AttributeValue> item)
    {
        var payload = item.TryGetValue(PayloadAttribute, out var payloadValue)
            ? payloadValue.S
            : null;

        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new InvalidOperationException("Order item is missing payload data.");
        }

        var order = JsonSerializer.Deserialize<Order>(payload);
        return order ?? throw new InvalidOperationException("Failed to deserialize order payload.");
    }
}

