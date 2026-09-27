namespace Utils.Workflows;

// A validated set of states, and the questions worth asking of one.
//
// TRANSITIONS ARE UNCONSTRAINED, DELIBERATELY. There is no allowed-transition graph here: any state
// may follow any other. Task trackers generally allow arbitrary moves, a deal can go back a stage,
// and a graph nobody asked for is speculative work that immediately becomes something to fight.
// Adding constraints later is purely additive - a consumer that needs them can hold its own edge
// table and check before calling. The one thing that WOULD have been painful to add later is
// Category, which is why that is here from the start.
public sealed class WorkflowDefinition<TState> where TState : class, IWorkflowState
{
    private readonly Dictionary<string, TState> _byKey;

    private WorkflowDefinition(IReadOnlyList<TState> states, Dictionary<string, TState> byKey, TState initial)
    {
        States = states;
        _byKey = byKey;
        InitialState = initial;
    }

    // In display order.
    public IReadOnlyList<TState> States { get; }

    public TState InitialState { get; }

    // Validates and builds, or explains why not. A result rather than an exception because the
    // states usually come from consumer-supplied data over an API, where "you sent two initial
    // states" is a 400 and not a crash.
    public static WorkflowValidationResult<TState> TryCreate(IEnumerable<TState> states)
    {
        var ordered = states.OrderBy(s => s.Ordinal).ToList();

        if (ordered.Count == 0)
        {
            return new WorkflowValidationResult<TState>.Invalid("A workflow needs at least one state.");
        }

        var duplicateKey = ordered
            .GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateKey is not null)
        {
            // Case-insensitive, because "Done" and "done" as two distinct states is never intended
            // and produces rows that look identical wherever they are displayed.
            return new WorkflowValidationResult<TState>.Invalid($"Duplicate state key '{duplicateKey.Key}'.");
        }

        var initial = ordered.Where(s => s.IsInitial).ToList();
        if (initial.Count != 1)
        {
            // Zero means a new item has nowhere to start; more than one means where it starts
            // depends on iteration order, which is the kind of bug that only shows up in
            // production once the rows come back in a different order.
            return new WorkflowValidationResult<TState>.Invalid(
                initial.Count == 0 ? "A workflow needs exactly one initial state; none was marked."
                                   : $"A workflow needs exactly one initial state; {initial.Count} were marked.");
        }

        if (ordered.All(s => s.Category == WorkflowStateCategory.Closed))
        {
            // Every state closed means nothing is ever in progress, which is a configuration
            // mistake rather than a workflow.
            return new WorkflowValidationResult<TState>.Invalid("A workflow needs at least one state that is not Closed.");
        }

        var byKey = ordered.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        return new WorkflowValidationResult<TState>.Valid(new WorkflowDefinition<TState>(ordered, byKey, initial[0]));
    }

    // Case-insensitive, matching the duplicate check above: a consumer that stored "Done" must not
    // fail to resolve it because a caller sent "done".
    public TState? Find(string? key) =>
        key is not null && _byKey.TryGetValue(key, out var state) ? state : null;

    public bool Contains(string? key) => Find(key) is not null;

    // "Is this item finished" without knowing the consumer's vocabulary - the question Category
    // exists to answer. An unknown key is NOT treated as closed: a row pointing at a state that no
    // longer exists is a data problem, and quietly reporting it as done would hide the work.
    public bool IsClosed(string? key) => Find(key)?.Category == WorkflowStateCategory.Closed;

    public bool IsOpen(string? key) => Find(key) is { } state && state.Category != WorkflowStateCategory.Closed;

    // Every state in a category, in display order - what a board column layout or a "count by
    // status" rollup is built from.
    public IReadOnlyList<TState> InCategory(WorkflowStateCategory category) =>
        States.Where(s => s.Category == category).ToList();
}

public abstract record WorkflowValidationResult<TState> where TState : class, IWorkflowState
{
    private WorkflowValidationResult() { }

    public sealed record Valid(WorkflowDefinition<TState> Definition) : WorkflowValidationResult<TState>;

    public sealed record Invalid(string Error) : WorkflowValidationResult<TState>;
}
