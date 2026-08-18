using Microsoft.EntityFrameworkCore;
using Utils.Queueing;

namespace Utils.Data.Queueing;

// Database-backed ILeasedQueue. Generic over the consuming service's own DbContext and message
// entity, resolved through Set<TMessage>(), so a service opts in by having its entity implement
// ILeasable and calling ConfigureLeasable() in OnModelCreating.
//
// **Why database-backed and not SQS**: the relay this exists for leases rows in the *same database*
// as the business write that produced them — that transactional coupling is the entire point of the
// outbox pattern. SQS cannot provide it, because you can't atomically write a business row and
// enqueue to a remote queue; that's the dual-write problem the outbox solves. SQS belongs
// DOWNSTREAM of a relay built on this, never in place of it. See Utils/CLAUDE.md's "Leased queue".
//
// Register as: services.AddScoped<ILeasedQueue<TMessage>, DbLeasedQueue<TContext, TMessage>>();
public class DbLeasedQueue<TContext, TMessage> : ILeasedQueue<TMessage>
    where TContext : DbContext
    where TMessage : class, ILeasable
{
    private readonly TContext _dbContext;

    public DbLeasedQueue(TContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<LeasedMessage<TMessage>>> TakeAsync(
        int maxCount, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;

        var candidates = await _dbContext.Set<TMessage>()
            .Where(m => m.CompletedAtUtc == null
                && m.DeadLetteredAtUtc == null
                && (m.LeasedUntilUtc == null || m.LeasedUntilUtc < now))
            .OrderBy(m => m.EnqueuedAtUtc)
            .Take(maxCount)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return [];
        }

        var leased = new List<LeasedMessage<TMessage>>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var token = Guid.NewGuid();
            var attemptCount = candidate.AttemptCount + 1;

            SetLeaseValues(candidate, token, now.Add(leaseDuration), attemptCount);

            leased.Add(new LeasedMessage<TMessage>(candidate, token, attemptCount));
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another consumer claimed at least one of these between the read and this write. The
            // whole batch is abandoned rather than picked apart: the contended rows are already
            // being worked by someone else, and the uncontended ones will still be sitting there on
            // the next poll. Returning empty is honest — this consumer took nothing.
            //
            // This needs the message entity to carry a concurrency token (see ConfigureLeasable),
            // and it needs the provider to enforce it. Any real relational provider does. EF Core's
            // InMemory provider does not enforce concurrency tokens across separate contexts, so
            // the contended case can't be reproduced there — see the note in
            // Utils.Tests/LeasedQueueTests.cs.
            _dbContext.ChangeTracker.Clear();
            return [];
        }

        return leased;
    }

    public async Task<bool> CompleteAsync(
        LeasedMessage<TMessage> leased, CancellationToken cancellationToken = default)
    {
        var current = await FindHeldAsync(leased, cancellationToken);
        if (current is null)
        {
            return false;
        }

        // Lease fields cleared alongside completion so a completed row can't be mistaken for one
        // holding a live lease, and so the take query's index stays useful.
        var entry = _dbContext.Entry(current);
        entry.Property(nameof(ILeasable.CompletedAtUtc)).CurrentValue = DateTimeOffset.UtcNow;
        entry.Property(nameof(ILeasable.LeaseToken)).CurrentValue = null;
        entry.Property(nameof(ILeasable.LeasedUntilUtc)).CurrentValue = null;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ReturnAsync(
        LeasedMessage<TMessage> leased, string? reason, int maxAttempts, CancellationToken cancellationToken = default)
    {
        var current = await FindHeldAsync(leased, cancellationToken);
        if (current is null)
        {
            return false;
        }

        // Compared against the row's own AttemptCount, not the caller's cached copy — the row is the
        // authority, and they only differ if something unexpected happened to the lease.
        var exhausted = current.AttemptCount >= maxAttempts;

        // Either way the lease is released without pushing LeasedUntilUtc into the future, so a
        // returned message is immediately available again rather than waiting out its original
        // lease. Dead-lettering additionally takes it out of the queue for good.
        var entry = _dbContext.Entry(current);
        entry.Property(nameof(ILeasable.LeaseToken)).CurrentValue = null;
        entry.Property(nameof(ILeasable.LeasedUntilUtc)).CurrentValue = null;

        if (exhausted)
        {
            entry.Property(nameof(ILeasable.DeadLetteredAtUtc)).CurrentValue = DateTimeOffset.UtcNow;
            entry.Property(nameof(ILeasable.DeadLetterReason)).CurrentValue = reason;
        }
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Values are set through the EF change tracker rather than this workspace's usual
    // `with { ... }` + CurrentValues.SetValues idiom: `with` requires the compiler to know the
    // receiver is a record, which it can't for an open generic type parameter. Setting properties
    // by name on the entry is the generic-safe equivalent and still respects `init` accessors,
    // since EF writes through the backing field.
    private void SetLeaseValues(TMessage message, Guid? token, DateTimeOffset? leasedUntil, int attemptCount)
    {
        var entry = _dbContext.Entry(message);
        entry.Property(nameof(ILeasable.LeaseToken)).CurrentValue = token;
        entry.Property(nameof(ILeasable.LeasedUntilUtc)).CurrentValue = leasedUntil;
        entry.Property(nameof(ILeasable.AttemptCount)).CurrentValue = attemptCount;

        // The concurrency token is a plain int this library maintains, not a database-generated
        // rowversion (see ConfigureLeasable for why). EF puts the ORIGINAL value in the UPDATE's
        // WHERE clause, so bumping it here is what makes a second consumer's write of the same row
        // match zero rows and raise DbUpdateConcurrencyException. Forgetting this line would not
        // fail any single-threaded test - it would silently disable leasing under contention.
        var version = entry.Property(VersionPropertyName);
        version.CurrentValue = (int)(version.CurrentValue ?? 0) + 1;
    }

    // Matches ConfigureLeasable's default. Both sides have to agree on the property name; it is a
    // parameter there only so an entity can deviate, and nothing in this workspace does.
    private const string VersionPropertyName = "Version";

    // Re-reads the row and confirms this caller still holds the lease it was given. A stale token
    // means the lease expired and someone else re-took it — completing or returning at that point
    // would stomp on the current holder's work.
    private async Task<TMessage?> FindHeldAsync(
        LeasedMessage<TMessage> leased, CancellationToken cancellationToken)
    {
        var id = leased.Message.Id;
        var current = await _dbContext.Set<TMessage>().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

        return current is not null && current.LeaseToken == leased.LeaseToken ? current : null;
    }
}
