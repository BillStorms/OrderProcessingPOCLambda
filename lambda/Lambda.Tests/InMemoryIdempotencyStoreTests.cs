using FluentAssertions;
using OrderProcessingSystemPOC.Shared.LambdaMigration.Shared.Idempotency;

namespace Lambda.Tests;

public class InMemoryIdempotencyStoreTests
{
    // ── Basic claim behaviour ────────────────────────────────────────────────

    [Fact]
    public async Task TryClaimAsync_FirstClaim_ReturnsTrue()
    {
        var store = new InMemoryIdempotencyStore();

        var result = await store.TryClaimAsync("evt-001");

        result.Should().BeTrue("first claim on a new eventId must succeed");
    }

    [Fact]
    public async Task TryClaimAsync_DuplicateClaim_ReturnsFalse()
    {
        var store = new InMemoryIdempotencyStore();
        await store.TryClaimAsync("evt-dup");

        var second = await store.TryClaimAsync("evt-dup");

        second.Should().BeFalse("duplicate claim on the same eventId must be rejected");
    }

    [Fact]
    public async Task TryClaimAsync_ThirdAndSubsequentCalls_AlwaysReturnFalse()
    {
        var store = new InMemoryIdempotencyStore();
        await store.TryClaimAsync("evt-repeat");
        await store.TryClaimAsync("evt-repeat"); // 2nd

        var third  = await store.TryClaimAsync("evt-repeat");
        var fourth = await store.TryClaimAsync("evt-repeat");

        third.Should().BeFalse();
        fourth.Should().BeFalse();
    }

    // ── Isolation between event IDs ──────────────────────────────────────────

    [Fact]
    public async Task TryClaimAsync_DifferentEventIds_AreIndependent()
    {
        var store = new InMemoryIdempotencyStore();

        var first  = await store.TryClaimAsync("evt-A");
        var second = await store.TryClaimAsync("evt-B");
        var third  = await store.TryClaimAsync("evt-C");

        first.Should().BeTrue();
        second.Should().BeTrue();
        third.Should().BeTrue();
    }

    [Fact]
    public async Task TryClaimAsync_DuplicateDoesNotAffectOtherIds()
    {
        var store = new InMemoryIdempotencyStore();
        // Claim and duplicate one ID
        await store.TryClaimAsync("evt-X");
        await store.TryClaimAsync("evt-X");

        // A brand-new ID should still succeed
        var fresh = await store.TryClaimAsync("evt-Y");

        fresh.Should().BeTrue("unrelated eventId must not be blocked by a duplicate of a different ID");
    }

    [Fact]
    public async Task TryClaimAsync_ManyUniqueIds_AllSucceed()
    {
        var store = new InMemoryIdempotencyStore();
        var ids   = Enumerable.Range(1, 50).Select(i => $"evt-{i:D3}").ToList();

        var results = new List<bool>();
        foreach (var id in ids)
            results.Add(await store.TryClaimAsync(id));

        results.Should().AllBeEquivalentTo(true,
            "each unique eventId should be claimable exactly once");
    }

    // ── Optional TTL parameter ───────────────────────────────────────────────

    [Fact]
    public async Task TryClaimAsync_WithExplicitTtl_FirstClaimStillReturnsTrue()
    {
        var store = new InMemoryIdempotencyStore();

        var result = await store.TryClaimAsync("evt-ttl", TimeSpan.FromMinutes(5));

        result.Should().BeTrue("TTL parameter must not prevent a valid first claim");
    }

    [Fact]
    public async Task TryClaimAsync_WithExplicitTtl_DuplicateStillReturnsFalse()
    {
        var store = new InMemoryIdempotencyStore();
        await store.TryClaimAsync("evt-ttl2", TimeSpan.FromMinutes(5));

        var duplicate = await store.TryClaimAsync("evt-ttl2", TimeSpan.FromMinutes(5));

        duplicate.Should().BeFalse("TTL parameter must not bypass duplicate detection");
    }

    // ── Concurrency ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TryClaimAsync_ConcurrentClaims_OnlyOneSucceeds()
    {
        var store   = new InMemoryIdempotencyStore();
        const int concurrency = 50;
        var tasks   = Enumerable.Range(0, concurrency)
                                .Select(_ => store.TryClaimAsync("evt-concurrent"))
                                .ToList();

        var results = await Task.WhenAll(tasks);

        results.Count(r => r)
               .Should().Be(1, "exactly one concurrent claim must win for the same eventId");
        results.Count(r => !r)
               .Should().Be(concurrency - 1, "all other concurrent claims must be rejected");
    }

    [Fact]
    public async Task TryClaimAsync_ConcurrentDifferentIds_AllSucceed()
    {
        var store = new InMemoryIdempotencyStore();
        var tasks = Enumerable.Range(0, 100)
                              .Select(i => store.TryClaimAsync($"evt-concurrent-{i}"))
                              .ToList();

        var results = await Task.WhenAll(tasks);

        results.Should().AllBeEquivalentTo(true,
            "concurrent claims for distinct eventIds must all succeed");
    }
}

