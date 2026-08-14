namespace Utils.Queueing;

// Contract an entity implements to be usable as queue work. The lease columns live on the message
// row itself rather than in a side table, so claiming is a single conditional update and there's no
// second thing to keep in sync.
//
// All the mutable members are `init` because this workspace models entities as records updated via
// `with { ... }` + Entry(...).CurrentValues.SetValues(...) rather than direct mutation.
public interface ILeasable
{
    Guid Id { get; }

    // FIFO ordering key. Deliberately the time the work became available, not the time it was last
    // attempted — a message that fails and is returned keeps its place in line rather than going to
    // the back, so one poison message can't indefinitely delay everything behind it.
    DateTimeOffset EnqueuedAtUtc { get; }

    // Non-null only while a consumer holds the lease. The token is what makes Complete/Return safe:
    // a consumer whose lease already expired and was re-taken by someone else presents a stale
    // token and is refused, rather than completing work another consumer is now doing.
    Guid? LeaseToken { get; init; }
    DateTimeOffset? LeasedUntilUtc { get; init; }

    // Incremented on every take, not on every failure — so a consumer that dies without returning
    // anything still counts against the dead-letter threshold. Otherwise a message that reliably
    // crashes its consumer would be retried forever, which is exactly the failure mode the
    // threshold exists to stop.
    int AttemptCount { get; init; }

    DateTimeOffset? CompletedAtUtc { get; init; }

    DateTimeOffset? DeadLetteredAtUtc { get; init; }
    string? DeadLetterReason { get; init; }
}

// A message plus the lease that must be presented to complete or return it.
public record LeasedMessage<TMessage>(TMessage Message, Guid LeaseToken, int AttemptCount)
    where TMessage : ILeasable;

// Take / complete / return, with lease expiry and dead-lettering.
//
// The two operations that are easy to leave out and are the ones that actually make this safe:
//   - **Lease expiry.** A consumer that dies holding a lease must have its work become visible
//     again without anyone calling Return. Without that, a crash strands work permanently — the
//     exact failure class this abstraction exists to prevent.
//   - **Dead-lettering.** Without an attempt ceiling, a permanently-failing message is retried
//     forever and starves the queue.
public interface ILeasedQueue<TMessage>
    where TMessage : class, ILeasable
{
    // Claims up to maxCount available messages for leaseDuration. Available means: not completed,
    // not dead-lettered, and either never leased or holding an expired lease. Returns fewer than
    // requested (including none) when that's all there is — an empty result is the normal idle case,
    // not an error.
    Task<IReadOnlyList<LeasedMessage<TMessage>>> TakeAsync(
        int maxCount, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    // Marks the work done. Returns false if the lease is no longer held (expired and re-taken by
    // another consumer), in which case the caller must NOT assume its side effect was the only one:
    // the other consumer is doing the same work. Better to know than to silently double-complete.
    Task<bool> CompleteAsync(
        LeasedMessage<TMessage> leased, CancellationToken cancellationToken = default);

    // Releases the lease early so the message becomes immediately available again, instead of
    // waiting out the lease. Dead-letters instead when the attempt count has reached maxAttempts.
    // Returns false on a stale lease, same as CompleteAsync.
    Task<bool> ReturnAsync(
        LeasedMessage<TMessage> leased, string? reason, int maxAttempts, CancellationToken cancellationToken = default);
}
