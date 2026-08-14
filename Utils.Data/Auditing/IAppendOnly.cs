namespace Utils.Data;

// Marker for entities that must never be updated or deleted once written. Enforced at save time by
// AppendOnlySaveChangesInterceptor.
//
// Deliberately a marker on the ENTITY rather than a property of the audit primitive specifically,
// so a service can opt any of its own immutable records in (an outbox row, an event log, a ledger
// entry) and get the same protection without adopting the audit shape.
//
// **This is a guardrail, not a security control.** It stops an honest mistake inside a DbContext
// that has the interceptor registered. It does not stop raw SQL, a different context, a DBA, or an
// attacker who already has database credentials. The real enforcement is a database grant that
// withholds UPDATE/DELETE on these tables from the application's runtime login - see
// "Append-only enforcement" in Utils/CLAUDE.md. Treat this as defense in depth, and don't let its
// presence justify skipping the grant.
public interface IAppendOnly
{
}
