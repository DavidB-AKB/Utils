namespace Utils.Data.Auditing;

public interface IAuditLogger
{
    // Adds the row to the EF change tracker only - deliberately never calls SaveChangesAsync
    // itself, so the audit row commits atomically with whatever business write triggered it rather
    // than as a second, independently-failable write. Callers must place the Log(...) call before
    // whichever SaveChangesAsync is meant to persist it.
    //
    // Audited *reads* (e.g. a moderator bypassing a visibility rule) and denials (nothing was
    // written to roll back with) have no business write to ride along with, so those callers are
    // responsible for saving explicitly.
    //
    // CorrelationId, IpAddress, and UserAgent are NOT parameters - the implementation fills them
    // from ambient context (Activity.Current and IAuditContextAccessor). Making them per-call
    // arguments would mean they go missing on exactly the paths that forget, which are the paths
    // that matter.
    //
    // For an event with no human actor, pass AuditEvent.SystemActorSubjectType as actorSubjectType
    // and the cause as actorSubjectId (e.g. "lockout"), rather than looking for a way to omit it.
    void Log(
        string eventType,
        string targetType,
        string targetId,
        string actorSubjectType,
        string actorSubjectId,
        IReadOnlyDictionary<string, string>? details = null,
        AuditOutcome outcome = AuditOutcome.Succeeded);
}
