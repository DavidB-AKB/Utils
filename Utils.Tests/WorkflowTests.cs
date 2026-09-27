using Utils.Workflows;

namespace Utils.Tests;

public class WorkflowTests
{
    // Stands in for a consumer's own mapped entity - Tasks' WorkflowState, a pipeline's Stage, a
    // ticket's status. The library never owns the table.
    private sealed record State(
        string Key, WorkflowStateCategory Category, int Ordinal, bool IsInitial = false) : IWorkflowState;

    private static WorkflowDefinition<State> Valid(params State[] states) =>
        Assert.IsType<WorkflowValidationResult<State>.Valid>(WorkflowDefinition<State>.TryCreate(states)).Definition;

    private static State[] TaskStates() =>
    [
        new("todo", WorkflowStateCategory.Open, 0, IsInitial: true),
        new("doing", WorkflowStateCategory.Active, 1),
        new("done", WorkflowStateCategory.Closed, 2),
    ];

    [Fact]
    public void AValidWorkflowExposesItsStatesInDisplayOrder()
    {
        var workflow = Valid(
            new State("done", WorkflowStateCategory.Closed, 2),
            new State("todo", WorkflowStateCategory.Open, 0, IsInitial: true),
            new State("doing", WorkflowStateCategory.Active, 1));

        Assert.Equal(new[] { "todo", "doing", "done" }, workflow.States.Select(s => s.Key));
    }

    [Fact]
    public void TheInitialStateIsWhereANewItemStarts() =>
        Assert.Equal("todo", Valid(TaskStates()).InitialState.Key);

    // Zero means a new item has nowhere to start; more than one means where it starts depends on
    // iteration order - a bug that only shows up once rows come back in a different order.
    [Fact]
    public void AWorkflowNeedsExactlyOneInitialState()
    {
        Assert.IsType<WorkflowValidationResult<State>.Invalid>(WorkflowDefinition<State>.TryCreate([
            new State("todo", WorkflowStateCategory.Open, 0),
            new State("done", WorkflowStateCategory.Closed, 1),
        ]));

        Assert.IsType<WorkflowValidationResult<State>.Invalid>(WorkflowDefinition<State>.TryCreate([
            new State("todo", WorkflowStateCategory.Open, 0, IsInitial: true),
            new State("doing", WorkflowStateCategory.Active, 1, IsInitial: true),
        ]));
    }

    [Fact]
    public void AWorkflowNeedsAtLeastOneState() =>
        Assert.IsType<WorkflowValidationResult<State>.Invalid>(WorkflowDefinition<State>.TryCreate([]));

    // Every state closed means nothing is ever in progress - a configuration mistake, not a
    // workflow.
    [Fact]
    public void AWorkflowNeedsAStateThatIsNotClosed() =>
        Assert.IsType<WorkflowValidationResult<State>.Invalid>(WorkflowDefinition<State>.TryCreate([
            new State("done", WorkflowStateCategory.Closed, 0, IsInitial: true),
        ]));

    // "Done" and "done" as two distinct states is never intended, and produces rows that look
    // identical wherever they are displayed.
    [Fact]
    public void DuplicateKeysAreRejectedCaseInsensitively() =>
        Assert.IsType<WorkflowValidationResult<State>.Invalid>(WorkflowDefinition<State>.TryCreate([
            new State("done", WorkflowStateCategory.Closed, 0, IsInitial: true),
            new State("Done", WorkflowStateCategory.Active, 1),
        ]));

    [Fact]
    public void LookupIsCaseInsensitive()
    {
        var workflow = Valid(TaskStates());

        // A consumer that stored "Done" must not fail to resolve it because a caller sent "done".
        Assert.NotNull(workflow.Find("DOING"));
        Assert.True(workflow.Contains("Todo"));
    }

    // The point of Category: these answers must not depend on knowing the consumer's vocabulary.
    [Fact]
    public void ClosednessIsAnsweredWithoutKnowingTheVocabulary()
    {
        var tasks = Valid(TaskStates());
        var pipeline = Valid(
            new State("prospect", WorkflowStateCategory.Open, 0, IsInitial: true),
            new State("negotiating", WorkflowStateCategory.Active, 1),
            new State("won", WorkflowStateCategory.Closed, 2),
            new State("lost", WorkflowStateCategory.Closed, 3));

        Assert.True(tasks.IsClosed("done"));
        Assert.True(pipeline.IsClosed("won"));

        // "Lost" is closed too. The category deliberately does not distinguish success from
        // failure - which it was is the state's own business, and splitting the category would
        // make every consumer's outcome vocabulary part of the enum.
        Assert.True(pipeline.IsClosed("lost"));
        Assert.False(pipeline.IsClosed("negotiating"));
    }

    // A row pointing at a state that no longer exists is a data problem. Reporting it as closed
    // would quietly hide the work rather than surfacing the inconsistency.
    [Fact]
    public void AnUnknownStateIsNeitherOpenNorClosed()
    {
        var workflow = Valid(TaskStates());

        Assert.False(workflow.IsClosed("deleted-state"));
        Assert.False(workflow.IsOpen("deleted-state"));
        Assert.False(workflow.IsClosed(null));
    }

    [Fact]
    public void StatesCanBeGroupedByCategory()
    {
        var pipeline = Valid(
            new State("prospect", WorkflowStateCategory.Open, 0, IsInitial: true),
            new State("won", WorkflowStateCategory.Closed, 1),
            new State("lost", WorkflowStateCategory.Closed, 2));

        Assert.Equal(new[] { "won", "lost" },
            pipeline.InCategory(WorkflowStateCategory.Closed).Select(s => s.Key));
    }

    // Transitions are unconstrained on purpose: a task tracker allows arbitrary moves and a deal
    // can go back a stage. A consumer needing a graph holds its own edge table.
    [Fact]
    public void AnyStateMayFollowAnyOther()
    {
        var workflow = Valid(TaskStates());

        // Nothing in the definition forbids going backwards - there is deliberately no API to ask.
        Assert.True(workflow.Contains("done"));
        Assert.True(workflow.Contains("todo"));
    }
}
