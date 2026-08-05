using System.Text;

namespace Utils;

// Cursor-based ("keyset") pagination primitive shared across every foundational service that
// exposes an unbounded list endpoint - added 2026-08-05 after a review found GET /users,
// /permissions, /roles, /assignments, /comments, /rules, and /templates all returned their entire
// result set with no limit. Originally lived in Permissions.Core (justified there by real,
// concrete duplication across 7 endpoints in 4 repos, not a speculative extraction), then moved
// here once a dedicated Utils repo existed - this has nothing to do with permissions/authz, and
// Permissions.Core was never meant to be the catch-all for every cross-cutting concern, just the
// ones that happened to land there first. See the top-level CLAUDE.md's "Cursor pagination" and
// "Utils" bullets for the full history.
//
// Keyset pagination, not offset (?page=2): offset pagination re-scans and skips rows on every
// page (expensive once a table is large) and is unstable under concurrent inserts/deletes - a row
// inserted before the current page shifts everything after it, silently skipping or duplicating
// rows on the next page. A cursor instead encodes the last row already seen, so the next page's
// query is "keep going from there" - stable and cheap regardless of table size.
public record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);

public static class PaginationDefaults
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    // A missing/non-positive limit falls back to DefaultLimit rather than erroring or (worse)
    // being treated as "unlimited" - the exact failure mode this feature exists to close. A
    // limit above MaxLimit is silently clamped down, not rejected, since "give me more than the
    // cap" isn't a caller error worth a 400 for.
    public static int ClampLimit(int? requested) =>
        requested is > 0 and <= MaxLimit ? requested.Value : requested > MaxLimit ? MaxLimit : DefaultLimit;
}

// Opaque to callers - a base64-encoded "<sortKey><id>" pair (\u001F = ASCII Unit Separator,
// chosen specifically because it can't be typed/pasted and won't show up in a role name or
// permission key by accident). sortKey is always a string the caller formats itself (e.g.
// DateTime.ToString("O") - ISO-8601 round-trip, which sorts lexicographically the same as
// chronologically), so this stays type-agnostic; each service parses its own sortKey back into
// whatever type it actually orders by.
public static class PaginationCursor
{
    private const char Separator = '\u001F';

    public static string Encode(string sortKey, string id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sortKey}{Separator}{id}"));

    // A null/empty/malformed cursor decodes to null rather than throwing - treated as "start from
    // the beginning," not a 400. Cursors are opaque tokens the caller round-trips, not something
    // they're expected to construct or validate themselves, so a corrupted one shouldn't error.
    //
    // Splits on the LAST separator, not the first - sortKey is caller/user-controlled data (a
    // role name, a permission key, ...) and could in principle contain the separator character,
    // but id never does (every id in this workspace is a Guid-backed strongly-typed ID rendered
    // via ToString()), so anchoring off the end is the direction that's actually guaranteed safe.
    public static (string SortKey, string Id)? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var separatorIndex = decoded.LastIndexOf(Separator);
            return separatorIndex > 0 && separatorIndex < decoded.Length - 1
                ? (decoded[..separatorIndex], decoded[(separatorIndex + 1)..])
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
