using Microsoft.EntityFrameworkCore;
using Utils.Data.Queueing;
using Utils.Queueing;

namespace Utils.Tests;

public class LeasedQueueTests
{
    private sealed record TestMessage : ILeasable
    {
        public Guid Id { get; init; }
        public string Payload { get; init; } = string.Empty;
        public DateTimeOffset EnqueuedAtUtc { get; init; }
        public Guid? LeaseToken { get; init; }
        public DateTimeOffset? LeasedUntilUtc { get; init; }
        public int AttemptCount { get; init; }
        public DateTimeOffset? CompletedAtUtc { get; init; }
        public DateTimeOffset? DeadLetteredAtUtc { get; init; }
        public string? DeadLetterReason { get; init; }
        public int Version { get; init; }
    }

    private sealed class TestQueueContext : DbContext
    {
        private readonly string _databaseName;

        public TestQueueContext(string databaseName) => _databaseName = databaseName;

        public DbSet<TestMessage> Messages => Set<TestMessage>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseInMemoryDatabase(_databaseName);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestMessage>().HasKey(m => m.Id);
            modelBuilder.ConfigureLeasable<TestMessage>();
        }
    }

    private static async Task<(TestQueueContext Db, DbLeasedQueue<TestQueueContext, TestMessage> Queue)> CreateAsync(
        params string[] payloads)
    {
        var db = new TestQueueContext(Guid.NewGuid().ToString());
        var enqueued = DateTimeOffset.UtcNow.AddMinutes(-payloads.Length);
        for (var i = 0; i < payloads.Length; i++)
        {
            db.Messages.Add(new TestMessage
            {
                Id = Guid.NewGuid(),
                Payload = payloads[i],
                EnqueuedAtUtc = enqueued.AddMinutes(i),
            });
        }

        await db.SaveChangesAsync();
        return (db, new DbLeasedQueue<TestQueueContext, TestMessage>(db));
    }

    [Fact]
    public async Task TakeAsync_ReturnsAvailableWorkOldestFirst()
    {
        var (db, queue) = await CreateAsync("first", "second", "third");

        var taken = await queue.TakeAsync(2, TimeSpan.FromMinutes(1));

        Assert.Equal(2, taken.Count);
        Assert.Equal("first", taken[0].Message.Payload);
        Assert.Equal("second", taken[1].Message.Payload);
    }

    [Fact]
    public async Task TakeAsync_DoesNotReturnWorkAlreadyLeasedByAnother()
    {
        var (db, queue) = await CreateAsync("only");

        var first = await queue.TakeAsync(10, TimeSpan.FromMinutes(5));
        var second = await queue.TakeAsync(10, TimeSpan.FromMinutes(5));

        Assert.Single(first);
        Assert.Empty(second);
    }

    // The property that makes a crash survivable: a consumer that dies holding a lease never calls
    // Return, so the work has to become visible again on its own.
    [Fact]
    public async Task TakeAsync_ReclaimsWorkWhoseLeaseExpired()
    {
        var (db, queue) = await CreateAsync("only");

        var first = await queue.TakeAsync(1, TimeSpan.FromMilliseconds(1));
        Assert.Single(first);
        await Task.Delay(30);

        var second = await queue.TakeAsync(1, TimeSpan.FromMinutes(1));

        Assert.Single(second);
        Assert.Equal("only", second[0].Message.Payload);
        // Attempts accumulate across the crash, so a message that reliably kills its consumer still
        // walks toward the dead-letter threshold instead of retrying forever.
        Assert.Equal(2, second[0].AttemptCount);
    }

    [Fact]
    public async Task CompleteAsync_RemovesWorkFromTheQueue()
    {
        var (db, queue) = await CreateAsync("only");
        var taken = await queue.TakeAsync(1, TimeSpan.FromMinutes(1));

        Assert.True(await queue.CompleteAsync(taken[0]));

        Assert.Empty(await queue.TakeAsync(10, TimeSpan.FromMinutes(1)));
        Assert.NotNull((await db.Messages.SingleAsync()).CompletedAtUtc);
    }

    // A consumer whose lease expired must not be able to complete work another consumer has since
    // taken over - it would mark done something still in flight elsewhere.
    [Fact]
    public async Task CompleteAsync_WithAStaleLease_IsRefused()
    {
        var (db, queue) = await CreateAsync("only");
        var stale = await queue.TakeAsync(1, TimeSpan.FromMilliseconds(1));
        await Task.Delay(30);
        await queue.TakeAsync(1, TimeSpan.FromMinutes(5));

        Assert.False(await queue.CompleteAsync(stale[0]));
        Assert.Null((await db.Messages.SingleAsync()).CompletedAtUtc);
    }

    [Fact]
    public async Task ReturnAsync_MakesWorkImmediatelyAvailableAgain()
    {
        var (db, queue) = await CreateAsync("only");
        var taken = await queue.TakeAsync(1, TimeSpan.FromMinutes(30));

        Assert.True(await queue.ReturnAsync(taken[0], "transient failure", maxAttempts: 5));

        // Available again without waiting out the original 30-minute lease.
        Assert.Single(await queue.TakeAsync(1, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task ReturnAsync_AtTheAttemptCeiling_DeadLetters()
    {
        var (db, queue) = await CreateAsync("poison");

        var taken = await queue.TakeAsync(1, TimeSpan.FromMinutes(1));
        Assert.True(await queue.ReturnAsync(taken[0], "always fails", maxAttempts: 1));

        // Dead-lettered work is out of the queue - otherwise a permanently-failing message starves
        // everything behind it.
        Assert.Empty(await queue.TakeAsync(10, TimeSpan.FromMinutes(1)));
        var message = await db.Messages.SingleAsync();
        Assert.NotNull(message.DeadLetteredAtUtc);
        Assert.Equal("always fails", message.DeadLetterReason);
        Assert.Null(message.CompletedAtUtc);
    }

    [Fact]
    public async Task ReturnAsync_BelowTheCeiling_RetriesRatherThanDeadLettering()
    {
        var (db, queue) = await CreateAsync("flaky");

        var taken = await queue.TakeAsync(1, TimeSpan.FromMinutes(1));
        await queue.ReturnAsync(taken[0], "transient", maxAttempts: 3);

        Assert.Null((await db.Messages.SingleAsync()).DeadLetteredAtUtc);
        Assert.Single(await queue.TakeAsync(1, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task TakeAsync_OnAnEmptyQueue_ReturnsNothingRatherThanFailing()
    {
        var (_, queue) = await CreateAsync();

        Assert.Empty(await queue.TakeAsync(10, TimeSpan.FromMinutes(1)));
    }

    // No automated test for two consumers racing to claim the SAME candidate row. TakeAsync's
    // DbUpdateConcurrencyException path depends on the provider enforcing the rowversion token
    // across separate DbContext instances, which SQL Server (the real target) does and EF Core's
    // InMemory provider — used throughout this class — does not. A test built on InMemory would let
    // both claims through and so would demonstrate a testing-infrastructure gap rather than verify
    // the guard. Same reasoning documented in Notifications' EventIngestionServiceTests and
    // Permissions' RoleAssignmentServiceTests for their own unique-index races.
}
