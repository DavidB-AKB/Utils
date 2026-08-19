using Microsoft.EntityFrameworkCore;
using Utils.Data.Queueing;
using Utils.Queueing;
using Utils.Tests.TestHelpers;

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
        private readonly string _connectionString;

        public TestQueueContext(string connectionString) => _connectionString = connectionString;

        public DbSet<TestMessage> Messages => Set<TestMessage>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder.UseNpgsql(_connectionString);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestMessage>().HasKey(m => m.Id);
            modelBuilder.ConfigureLeasable<TestMessage>();
        }
    }

    // EnsureCreated rather than a migration: this library owns no DbContext, so the context above
    // exists only here and has no migration chain of its own.
    private static async Task<(TestQueueContext Db, DbLeasedQueue<TestQueueContext, TestMessage> Queue)> CreateAsync(
        params string[] payloads)
    {
        var db = new TestQueueContext(TestPostgres.CreateDatabase());
        await db.Database.EnsureCreatedAsync();

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

    // Two consumers racing to claim the SAME candidate row - the guard the whole design rests on,
    // and previously untestable: EF Core's InMemory provider does not enforce concurrency tokens
    // across separate contexts, so both claims went through and a test built on it would have
    // demonstrated a testing-infrastructure gap rather than verified anything.
    //
    // The race is forced rather than timed, so this can't flake. The loser reads the row BEFORE the
    // winner claims it (that read is what captures the original version), and writes after - which
    // is exactly the read-then-write window TakeAsync is chatty about in Utils/CLAUDE.md's "Known
    // limit". EF returns the already-tracked instance for an entity it has seen, without refreshing
    // it, so the loser's second read keeps that stale original version; a short lease on the
    // winner's claim is only there to get the row back past the take query's availability filter.
    [Fact]
    public async Task TakeAsync_WhenAnotherConsumerClaimedTheSameRowFirst_TakesNothing()
    {
        var (winnerDb, winnerQueue) = await CreateAsync("contended");
        await using var loserDb = new TestQueueContext(winnerDb.Database.GetConnectionString()!);
        var loserQueue = new DbLeasedQueue<TestQueueContext, TestMessage>(loserDb);

        await loserDb.Messages.ToListAsync();

        var winner = await winnerQueue.TakeAsync(1, TimeSpan.FromMilliseconds(1));
        Assert.Single(winner);
        await Task.Delay(30);

        Assert.Empty(await loserQueue.TakeAsync(1, TimeSpan.FromMinutes(1)));

        // And the winner's lease is intact - a rejected claim must not half-apply and leave the row
        // stamped with the loser's token.
        var row = await winnerDb.Messages.AsNoTracking().SingleAsync();
        Assert.Equal(winner[0].LeaseToken, row.LeaseToken);
    }
}
