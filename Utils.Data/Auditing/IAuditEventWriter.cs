namespace Utils.Data.Auditing;

// Writes an audit row AND commits it, for events that have no business write to ride along with.
//
// This is the deliberate counterpart to IAuditLogger, not a replacement for it. IAuditLogger never
// saves, precisely so an audit row commits atomically with whatever it describes - that property is
// the reason it exists and must not be weakened. But a *denial* describes something that did not
// happen: nothing was written, so there is no transaction to join, and a Log() call with no
// following SaveChangesAsync would simply be discarded when the scope ends. Hence a second, honest
// contract rather than a save on the first one.
//
// **Only call this when nothing else in the current scope has pending changes.** It saves the whole
// unit of work, not just the audit row, so calling it midway through a business operation would
// commit that operation early. The intended callers are authorization refusals, which run before
// any endpoint work has begun, and read-path denials, which by definition wrote nothing.
public interface IAuditEventWriter
{
    Task WriteAsync(
        string eventType,
        string targetType,
        string targetId,
        string actorSubjectType,
        string actorSubjectId,
        IReadOnlyDictionary<string, string>? details = null,
        AuditOutcome outcome = AuditOutcome.Denied,
        CancellationToken cancellationToken = default);
}
