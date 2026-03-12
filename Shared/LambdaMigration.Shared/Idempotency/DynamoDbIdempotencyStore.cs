using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace OrderProcessingSystemPOC.Shared.LambdaMigration.Shared.Idempotency;

/// <summary>
/// DynamoDB-backed idempotency store for production Lambda use.
/// Uses a conditional PutItem so only the first caller for a given eventId succeeds.
/// Table schema: PK = eventId (String), TTL attribute = ExpiresAt (Number, epoch seconds).
/// </summary>
public sealed class DynamoDbIdempotencyStore : IIdempotencyStore
{
    private readonly IAmazonDynamoDB _dynamo;
    private readonly string _tableName;

    public DynamoDbIdempotencyStore(IAmazonDynamoDB dynamo, string tableName = "order-idempotency")
    {
        _dynamo    = dynamo;
        _tableName = tableName;
    }

    public async Task<bool> TryClaimAsync(string eventId, TimeSpan? ttl = null)
    {
        var expiresAt = DateTimeOffset.UtcNow
            .Add(ttl ?? TimeSpan.FromDays(7))
            .ToUnixTimeSeconds();

        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["EventId"]   = new AttributeValue { S = eventId },
                ["ExpiresAt"] = new AttributeValue { N = expiresAt.ToString() },
                ["ClaimedAt"] = new AttributeValue { S = DateTime.UtcNow.ToString("O") }
            },
            // Only succeed if no item with this EventId exists yet
            ConditionExpression = "attribute_not_exists(EventId)"
        };

        try
        {
            await _dynamo.PutItemAsync(request);
            return true;  // successfully claimed — first time we've seen this eventId
        }
        catch (ConditionalCheckFailedException)
        {
            return false; // already processed — skip
        }
    }
}

