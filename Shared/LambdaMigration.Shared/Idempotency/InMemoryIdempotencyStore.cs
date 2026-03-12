using System.Collections.Concurrent;

namespace OrderProcessingSystemPOC.Shared.LambdaMigration.Shared.Idempotency;

/// <summary>In-memory idempotency store — use for local dev and unit tests only.</summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, DateTime> _seen = new();

    public Task<bool> TryClaimAsync(string eventId, TimeSpan? ttl = null)
    {
        var claimed = _seen.TryAdd(eventId, DateTime.UtcNow);
        return Task.FromResult(claimed);
    }
}

