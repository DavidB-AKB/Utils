namespace Utils.Workflows;

// How a state counts when nobody knows the consumer's vocabulary.
//
// THE LOAD-BEARING PART OF THIS LIBRARY. Every cross-workflow question - "how much is still open",
// "show me what isn't done", "how long did this spend in progress" - has to work without knowing
// whether a consumer calls its states todo/doing/done or prospect/negotiating/won. Grouping by a
// free-text state key cannot answer any of them, and a reporting layer that tried would need a
// hardcoded list of every consumer's vocabulary.
public enum WorkflowStateCategory
{
    // Not started. Work waiting to be picked up; a deal not yet engaged with.
    Open = 0,

    // Started and not finished. The only category where "how long has this been here" is usually
    // the interesting question.
    Active = 1,

    // Finished, by any route. Deliberately does NOT distinguish success from failure - "done" and
    // "cancelled" and "lost" are all closed, and which of them it was is the state's own business.
    // Splitting the category instead would make every consumer's outcome vocabulary part of this
    // enum, which is exactly what it exists to avoid.
    Closed = 2,
}

// Contract a consumer's own state entity implements to be usable as a workflow state.
//
// The consumer maps its own table - same arrangement as Utils.Queueing.ILeasable and
// Utils.Data.Auditing's AuditEvent: a shared shape and shared logic, deliberately NOT a shared
// service or a shared table. Foundational services may not reference each other, so a library both
// sides reference is the established way for work items, deal pipelines and support tickets to
// share one state machine without any of them knowing about the others.
public interface IWorkflowState
{
    // Stable identifier the consumer stores on its rows. A display name can change freely; this
    // cannot, because rows point at it.
    string Key { get; }

    WorkflowStateCategory Category { get; }

    // Display order within the workflow. Not a transition constraint - see WorkflowDefinition.
    int Ordinal { get; }

    // Where a newly created item starts. Exactly one per workflow.
    bool IsInitial { get; }
}
