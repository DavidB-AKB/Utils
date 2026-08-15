namespace Utils.Messaging;

// The contract a producing service's outbox relay hands a fact to, and that a consuming service
// implements to receive one. Neither side references the other: both reference this.
//
// This exists so wiring a producer to a consumer costs no adapter code in the combined project that
// hosts them. Before it, Users' relay spoke ITopicPublisher/OutboxMessage and Notifications' inbound
// side spoke IEventIngestionService/EventEnvelope, so whatever hosted both had to hand-write a
// mapping between two near-identical shapes — and that mapping encoded the consumer's wire format
// in a third place that nothing tested. Now a consumer ships its own IFactPublisher and the host
// just registers it.
//
// This does NOT weaken the outbox pattern or the workspace's no-references-between-services rule.
// The pattern's guarantees are that the fact commits atomically with the business write and is
// delivered at-least-once afterwards; neither depends on the transport being networked. And
// depending on a shared abstraction that neither service owns is dependency inversion, not
// coupling — the alternative (a project reference from one service to the other) is what the
// convention actually forbids.
public record FactEnvelope
{
    // The producer's own message id, passed through unchanged. This is the deduplication key: a
    // consumer is expected to see the same MessageId more than once, because at-least-once is what
    // a transactional outbox can promise (a crash between "delivered" and "marked complete"
    // redelivers). Consumers dedupe on this; producers must not regenerate it per attempt.
    public Guid MessageId { get; init; }

    // What happened, as a string both sides agree on out of band (e.g. "user.password_reset_requested").
    // Deliberately not an enum: a consumer registers reactions to event types as data, and a new
    // producer event must not require a code change in a shared library to be expressible.
    public string EventType { get; init; } = string.Empty;

    // When the fact occurred, not when it was delivered. For an outbox row these are the same
    // instant the business write committed, which is what makes the value meaningful to a consumer
    // that receives it minutes later after a retry.
    public DateTimeOffset OccurredAtUtc { get; init; }

    // WHO the fact concerns, as an opaque (type, id) pair — the same pattern Permissions uses for
    // grant subjects and Comments for targets. "user" plus a user id, "operator" plus a mailbox
    // name, anything the producer and consumer agree on.
    //
    // This replaced a single Recipient string that held an email address. An address is not an
    // identity: it changes, it can't express "reach them by SMS", and nothing durable can be
    // attached to it — so preferences, channel selection, and delivery history all had nowhere to
    // live. Naming the subject instead lets a consumer keep its own record of how to reach them.
    public string SubjectType { get; init; } = string.Empty;
    public string SubjectId { get; init; } = string.Empty;

    // OPTIONAL hint for how to reach the subject right now, in the producer's natural channel (an
    // email address today). Not the address of record — the consumer owns that — but a producer
    // usually knows a current one, and a consumer that has never seen this subject before would
    // otherwise have nothing to deliver to.
    //
    // Because it rides along on every fact, ordinary traffic keeps the consumer's stored contact
    // details fresh: a changed address propagates on the next notification rather than needing a
    // synchronisation job between two services that deliberately don't reference each other.
    public string? ContactAddress { get; init; }

    // The fact's payload, flat string-to-string. Flat because the primary consumer substitutes these
    // into templates, and a nested structure would need a traversal syntax to be useful there.
    // Anything richer belongs in the producer's own data, not in a fact.
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();
}

// Implemented by a consuming service, depended on by a producing service's relay.
//
// The contract is deliberately "returns normally = handled, throws = retry", with no success/failure
// return value. A consumer that recognises a MessageId it has already processed must return
// normally, because a duplicate IS the expected outcome of at-least-once delivery, not a failure.
// Modelling that as a `false` return invites a publisher to treat it as one and hand the message
// back to the queue, which would eventually dead-letter something that was in fact delivered — so
// the shape makes that mistake unrepresentable rather than merely documenting against it.
//
// Throwing is the only way to ask for a retry, and it should be reserved for genuinely transient
// failures. A permanently un-processable fact should be recorded and accepted, not thrown on
// forever: the producer's queue will dead-letter it after its attempt ceiling either way, and a
// dead-lettered row is a worse place to discover the problem than the consumer's own logs.
public interface IFactPublisher
{
    Task PublishAsync(FactEnvelope fact, CancellationToken cancellationToken = default);
}
