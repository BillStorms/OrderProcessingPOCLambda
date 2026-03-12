namespace OrderProcessingSystemPOC.Shared.LambdaMigration.Shared.Idempotency;

/// <summary>
/// Store that tracks processed event IDs to prevent duplicate handling.
/// Implementations: DynamoDbIdempotencyStore (production), InMemoryIdempotencyStore (tests/local).
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Tries to claim an event ID. Returns true if this is the FIRST time we've seen it
    /// (caller should process). Returns false if it was already processed (caller should skip).
    /// </summary>
    Task<bool> TryClaimAsync(string eventId, TimeSpan? ttl = null);
}

