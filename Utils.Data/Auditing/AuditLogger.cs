using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Utils.Data.Auditing;

// Generic over the consuming service's own DbContext, resolved via Set<AuditEvent>() rather than
// requiring that context to expose a DbSet property or implement an interface - so a service opts
// in purely by registering AuditLogger<ItsOwnContext> and calling ConfigureAuditEvents() in
// OnModelCreating. Each service's rows live in its own schema; nothing here is centralized.
//
// Register as: services.AddScoped<IAuditLogger, AuditLogger<TheServicesDbContext>>();
// Registering IAuditEventWriter as well is what enables denial auditing - see that interface, and
// note both registrations must resolve the SAME scoped instance so they share one DbContext.
public class AuditLogger<TContext> : IAuditLogger, IAuditEventWriter
    where TContext : DbContext
{
    private readonly TContext _dbContext;
    private readonly IAuditContextAccessor _contextAccessor;

    // The accessor is optional so a test or a background job can construct this with just a
    // context; it falls back to recording no network context rather than failing.
    public AuditLogger(TContext dbContext, IAuditContextAccessor? contextAccessor = null)
    {
        _dbContext = dbContext;
        _contextAccessor = contextAccessor ?? new NullAuditContextAccessor();
    }

    public void Log(
        string eventType,
        string targetType,
        string targetId,
        string actorSubjectType,
        string actorSubjectId,
        IReadOnlyDictionary<string, string>? details = null,
        AuditOutcome outcome = AuditOutcome.Succeeded)
    {
        var context = _contextAccessor.Current;

        _dbContext.Set<AuditEvent>().Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            TargetType = targetType,
            TargetId = targetId,
            ActorSubjectType = actorSubjectType,
            ActorSubjectId = actorSubjectId,
            Outcome = outcome,

            // W3C trace id, propagated across service boundaries by ASP.NET Core's own
            // traceparent handling - so two audit rows written by two different services during
            // one logical operation share a correlation id with no explicit wiring.
            CorrelationId = Truncate(Activity.Current?.TraceId.ToString(), AuditEvent.MaxCorrelationIdLength),

            // Truncated rather than rejected: a client fully controls its User-Agent, and failing
            // an audit write (which would roll back the business operation it rides along with)
            // because someone sent a 10KB header would turn a logging concern into an availability
            // problem. The audit row is worth more than the tail of the string.
            IpAddress = Truncate(context?.IpAddress, AuditEvent.MaxIpAddressLength),
            UserAgent = Truncate(context?.UserAgent, AuditEvent.MaxUserAgentLength),

            DetailsJson = details is null || details.Count == 0 ? null : JsonSerializer.Serialize(details),
            OccurredAtUtc = DateTimeOffset.UtcNow,
        });
    }

    // See IAuditEventWriter: for a denial there is no business write to commit alongside, so this
    // one saves. AuditOutcome.Denied is the default here (and Succeeded is the default on Log)
    // because that asymmetry matches why each exists - you reach for this one when something was
    // refused.
    public async Task WriteAsync(
        string eventType,
        string targetType,
        string targetId,
        string actorSubjectType,
        string actorSubjectId,
        IReadOnlyDictionary<string, string>? details = null,
        AuditOutcome outcome = AuditOutcome.Denied,
        CancellationToken cancellationToken = default)
    {
        Log(eventType, targetType, targetId, actorSubjectType, actorSubjectId, details, outcome);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength];
}
