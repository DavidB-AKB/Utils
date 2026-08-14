using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Utils.Data;

// Blocks any attempt to update or delete an IAppendOnly entity, at the point SaveChanges runs.
// Register on each consuming context: optionsBuilder.AddInterceptors(new AppendOnlySaveChangesInterceptor()).
//
// Throws rather than silently discarding the change - a caller that tried to mutate an audit row
// has a bug, and quietly dropping the mutation would hide it while still leaving the caller
// believing the write succeeded.
//
// See IAppendOnly for the (important) limits of what this actually protects against.
public sealed class AppendOnlySaveChangesInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        ThrowIfAppendOnlyViolated(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ThrowIfAppendOnlyViolated(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void ThrowIfAppendOnlyViolated(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is not IAppendOnly)
            {
                continue;
            }

            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"{entry.Entity.GetType().Name} is append-only and cannot be " +
                    $"{(entry.State == EntityState.Modified ? "modified" : "deleted")}. " +
                    "See Utils.Data.IAppendOnly.");
            }
        }
    }
}
