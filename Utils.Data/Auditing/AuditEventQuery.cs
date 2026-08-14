using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Utils.Data.Auditing;

public static class AuditEventQuery
{
    // Applies the keyset cursor, ordering, and paging shared by every service's audit-log endpoint.
    // Takes an already-filtered IQueryable so each service keeps its own filters (Comments scopes
    // by targetType/targetId, Permissions also browses unfiltered) without this helper needing to
    // know about any of them.
    //
    // Ordered newest-first - an audit log is read backwards from the most recent activity - with Id
    // descending as the keyset tiebreak. Permissions and Comments previously used opposite tiebreak
    // directions (each internally consistent, but different); unified here on fully-descending,
    // which is the one that matches how the page is actually read.
    public static async Task<CursorPage<AuditEvent>> ToPageAsync(
        IQueryable<AuditEvent> query,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var take = PaginationDefaults.ClampLimit(limit);

        if (PaginationCursor.Decode(cursor) is { } decoded)
        {
            var cursorOccurredAt = DateTimeOffset.Parse(decoded.SortKey, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var cursorId = Guid.Parse(decoded.Id);
            query = query.Where(e =>
                e.OccurredAtUtc < cursorOccurredAt
                || (e.OccurredAtUtc == cursorOccurredAt && e.Id.CompareTo(cursorId) < 0));
        }

        var page = await query
            .OrderByDescending(e => e.OccurredAtUtc).ThenByDescending(e => e.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);

        var hasMore = page.Count > take;
        var items = hasMore ? page.Take(take).ToList() : page;
        var nextCursor = hasMore
            ? PaginationCursor.Encode(items[^1].OccurredAtUtc.ToString("O", CultureInfo.InvariantCulture), items[^1].Id.ToString())
            : null;

        return new CursorPage<AuditEvent>(items, nextCursor);
    }
}
