using Microsoft.EntityFrameworkCore;
using Utils.Queueing;

namespace Utils.Data.Queueing;

public static class LeasableModelBuilderExtensions
{
    // Call from OnModelCreating for each ILeasable entity. Adds the indexes the take query needs
    // and the concurrency token the claim depends on.
    //
    // rowVersionColumn is the property name of a byte[] concurrency token on the entity. It's
    // required rather than optional: without it, two consumers reading the same candidate row would
    // both successfully stamp their own lease, and both would process the message — which defeats
    // the point of leasing. Making it a parameter rather than inferring it keeps this from silently
    // doing nothing when an entity forgets to declare one.
    public static ModelBuilder ConfigureLeasable<TMessage>(
        this ModelBuilder modelBuilder, string rowVersionColumn = "RowVersion")
        where TMessage : class, ILeasable
    {
        modelBuilder.Entity<TMessage>(entity =>
        {
            entity.Property(rowVersionColumn).IsRowVersion();

            entity.Property(m => m.DeadLetterReason).HasMaxLength(1000);

            // The take query's predicate: available work, oldest first. Covers
            // (CompletedAtUtc, DeadLetteredAtUtc, LeasedUntilUtc) with EnqueuedAtUtc for the sort,
            // so a poll doesn't scan completed history that only grows.
            entity.HasIndex(m => new { m.CompletedAtUtc, m.DeadLetteredAtUtc, m.LeasedUntilUtc, m.EnqueuedAtUtc });

            // "What's stuck in the dead-letter queue" - the operational question that has to be
            // answerable, or dead-lettering just becomes a quieter way of losing messages.
            entity.HasIndex(m => m.DeadLetteredAtUtc);
        });

        return modelBuilder;
    }
}
