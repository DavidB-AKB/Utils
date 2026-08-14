namespace Utils.Data.Auditing;

// Whether the audited action actually happened or was refused. Denials are worth recording
// precisely because they're the interesting security signal - a successful permission grant is
// routine, a stream of refused ones is not. Kept a closed two-value enum rather than string
// constants (unlike EventType): "did it happen" is genuinely binary and isn't something a
// consuming service should be extending.
public enum AuditOutcome
{
    Succeeded = 0,
    Denied = 1,
}

// Append-only record of a single audited action. Shared across every service that keeps an audit
// trail - Users, Permissions, and Comments each grew a near-identical copy independently before
// this was extracted, which is the duplication that justified pulling it out (see the top-level
// CLAUDE.md's bar: real duplication, not speculative).
//
// Each service still maps this into its OWN schema and owns its OWN rows - this is a shared shape
// and a shared writer, not a centralized audit database. There is deliberately no cross-service
// audit query: "who was granted admin" is a Permissions question and "who read my direct messages"
// is a Comments question, and nothing has needed to ask both at once. CorrelationId is what ties
// events together across services when one logical operation spans several.
//
// TargetType/TargetId identify what was acted on, as opaque strings rather than a typed union,
// since one log necessarily spans several entity kinds ("permission"/PermissionId,
// "comment"/CommentId, "user"/UserId, "query"/a serialized filter). Same opaque-reference pattern
// Permissions uses for SubjectType/SubjectId and Comments uses for its own TargetType/TargetId.
//
// Actor fields are non-nullable. Genuinely actor-less events - an automatic lockout, refresh-token
// reuse detection - use a sentinel ("system", <cause>) actor rather than null. That's strictly
// stronger than a nullable column: it's self-describing, it's queryable ("show me everything the
// system did on its own"), and it means no reader has to handle a null actor.
public record AuditEvent : IAppendOnly
{
    public const int MaxEventTypeLength = 100;
    public const int MaxTargetTypeLength = 50;
    public const int MaxTargetIdLength = 200;
    public const int MaxActorSubjectTypeLength = 50;
    public const int MaxActorSubjectIdLength = 200;
    public const int MaxCorrelationIdLength = 100;
    public const int MaxIpAddressLength = 45;   // fits a full IPv6 address
    public const int MaxUserAgentLength = 512;

    // The sentinel actor subject type for events with no human actor - see the class comment.
    public const string SystemActorSubjectType = "system";

    public Guid Id { get; init; }

    // A service-defined constant, not an enum - a reader should degrade gracefully on an
    // unrecognized value rather than break deserialization when a new event type ships. Each
    // service keeps its own constants (UsersMicroservice.Models.AuditEventTypes,
    // Permissions.Models.AuditEventTypes, CommentsService.Models.AuditEventTypes); there is no
    // shared vocabulary here on purpose, since the events a service records are exactly as
    // service-specific as its domain.
    public required string EventType { get; init; }

    public required string TargetType { get; init; }
    public required string TargetId { get; init; }

    public required string ActorSubjectType { get; init; }
    public required string ActorSubjectId { get; init; }

    public AuditOutcome Outcome { get; init; } = AuditOutcome.Succeeded;

    // Ties together every event produced by one logical operation, including across services -
    // populated from the ambient System.Diagnostics.Activity trace id, which ASP.NET Core
    // propagates via the W3C traceparent header. Null outside any traced operation.
    public string? CorrelationId { get; init; }

    // Where the actor acted from, when there's a request to read it off. Null for background work.
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }

    // Serialized Dictionary<string,string> - null when an event type has nothing worth recording
    // beyond who/what/when.
    public string? DetailsJson { get; init; }

    public DateTimeOffset OccurredAtUtc { get; init; }
}
