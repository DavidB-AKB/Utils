namespace Utils.Data.Auditing;

// Ambient request context worth recording alongside a security-relevant audit event. Supplied by
// the host rather than passed at every call site, deliberately: threading an IP address through
// every service method that might log would be invasive, and relying on each caller to remember it
// guarantees it goes missing exactly on the paths that matter.
//
// CorrelationId is NOT here - AuditLogger reads it from System.Diagnostics.Activity.Current, which
// ASP.NET Core populates from the W3C traceparent header and propagates across service boundaries
// automatically. That gets cross-service correlation with no wiring and no framework dependency.
public record AuditContext(string? IpAddress, string? UserAgent);

public interface IAuditContextAccessor
{
    AuditContext? Current { get; }
}

// Default when nothing better is registered - background jobs, console tooling, and tests have no
// request context, and that's a legitimate state rather than an error. Audit rows written through
// it simply carry null network fields.
public sealed class NullAuditContextAccessor : IAuditContextAccessor
{
    public AuditContext? Current => null;
}
