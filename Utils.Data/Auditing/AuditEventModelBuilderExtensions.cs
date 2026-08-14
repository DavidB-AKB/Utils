using Microsoft.EntityFrameworkCore;

namespace Utils.Data.Auditing;

public static class AuditEventModelBuilderExtensions
{
    // Call from OnModelCreating. The table lands in whatever HasDefaultSchema the calling context
    // set, so each service's audit rows stay in its own schema - see the top-level CLAUDE.md's
    // "Shared database, per-service schema" convention.
    //
    // No foreign key to anything, deliberately: an audit row has to outlive whatever it describes.
    // A permission.deleted event must stay queryable after the permission is gone, and TargetId
    // isn't always an entity id anyway (Comments records a serialized query for a list read).
    public static ModelBuilder ConfigureAuditEvents(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.EventType).IsRequired().HasMaxLength(AuditEvent.MaxEventTypeLength);
            entity.Property(e => e.TargetType).IsRequired().HasMaxLength(AuditEvent.MaxTargetTypeLength);
            entity.Property(e => e.TargetId).IsRequired().HasMaxLength(AuditEvent.MaxTargetIdLength);
            entity.Property(e => e.ActorSubjectType).IsRequired().HasMaxLength(AuditEvent.MaxActorSubjectTypeLength);
            entity.Property(e => e.ActorSubjectId).IsRequired().HasMaxLength(AuditEvent.MaxActorSubjectIdLength);
            entity.Property(e => e.OccurredAtUtc).IsRequired();
            entity.Property(e => e.Outcome).IsRequired();
            entity.Property(e => e.CorrelationId).HasMaxLength(AuditEvent.MaxCorrelationIdLength);
            entity.Property(e => e.IpAddress).HasMaxLength(AuditEvent.MaxIpAddressLength);
            entity.Property(e => e.UserAgent).HasMaxLength(AuditEvent.MaxUserAgentLength);

            // "Everything that happened to this entity," newest first.
            entity.HasIndex(e => new { e.TargetType, e.TargetId, e.OccurredAtUtc });

            // "Everything this actor did" - the accountability query the table exists for.
            entity.HasIndex(e => new { e.ActorSubjectType, e.ActorSubjectId });

            // "Everything that happened during this one logical operation," including the parts
            // other services recorded. Deliberately unfiltered: a filtered index excluding nulls
            // would be slightly smaller, but HasFilter is SQL Server-specific and this project
            // stays provider-agnostic (it references only Microsoft.EntityFrameworkCore, no
            // provider). A service that wants the filtered version can add it in its own
            // OnModelCreating after calling this.
            entity.HasIndex(e => e.CorrelationId);

            // "Show me refused attempts, newest first" - the query that makes recording denials
            // worth doing, and the one a security review actually runs.
            entity.HasIndex(e => new { e.Outcome, e.OccurredAtUtc });
        });

        return modelBuilder;
    }
}
