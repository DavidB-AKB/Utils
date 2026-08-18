using Microsoft.EntityFrameworkCore;
using Utils.Queueing;

namespace Utils.Data.Queueing;

public static class LeasableModelBuilderExtensions
{
    // Call from OnModelCreating for each ILeasable entity. Adds the indexes the take query needs
    // and the concurrency token the claim depends on.
    //
    // versionColumn is the property name of an int concurrency token on the entity. It's required
    // rather than optional: without it, two consumers reading the same candidate row would both
    // successfully stamp their own lease, and both would process the message — which defeats the
    // point of leasing. Making it a parameter rather than inferring it keeps this from silently
    // doing nothing when an entity forgets to declare one.
    //
    // A plain int the application increments (see DbLeasedQueue.SetLeaseValues), NOT SQL Server's
    // IsRowVersion(). rowversion is a SQL Server type with no Postgres equivalent — Npgsql's
    // closest analogue is mapping the system xmin column, which is provider-specific API. An
    // app-incremented token behaves identically on both engines and keeps this project referencing
    // nothing but Microsoft.EntityFrameworkCore, the same provider-agnostic bar the audit
    // primitive's own "HasFilter is SQL Server-specific" note applies.
    public static ModelBuilder ConfigureLeasable<TMessage>(
        this ModelBuilder modelBuilder, string versionColumn = "Version")
        where TMessage : class, ILeasable
    {
        modelBuilder.Entity<TMessage>(entity =>
        {
            entity.Property(versionColumn).IsConcurrencyToken();

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
