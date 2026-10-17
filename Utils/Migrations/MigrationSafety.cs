using System.Text;
using System.Text.RegularExpressions;

namespace Utils.Migrations;

// Static analysis of EF Core migration source, so "additive-only by default" is a check rather than
// a paragraph.
//
// WHY THIS EXISTS. Once a customer is running real data, every migration is an upgrade against a
// database nobody here can inspect, and the failure mode is silent and permanent: a dropped column
// takes its contents with it, and the first anyone knows is a customer asking where something went.
// A stated policy does not catch that; a test does.
//
// WHY SOURCE TEXT RATHER THAN THE EF MODEL. The operations that matter are the ones written into the
// generated migration, and they are all plain calls on `migrationBuilder`. Reading the source needs
// no EF Core reference (so this lives in `Utils`, which has none), runs in milliseconds, and catches
// hand-edited migrations - which is exactly where a destructive change is most likely to be added,
// because the scaffolder did not put it there.
//
// ONLY `Up()` IS ANALYSED. `Down()` is destructive by definition - undoing a migration is what it is
// for - and flagging it would make every `InitialCreate` in the workspace fail. That distinction is
// the whole reason this is worth writing rather than grepping: a naive grep reports 70-odd
// operations across these repos, of which exactly one is in an `Up()`.
public static class MigrationSafety
{
    // The marker an author writes to accept a flagged operation. Deliberately verbose and
    // greppable: the point is that somebody states, in the migration itself, that they thought
    // about the data already in the table.
    public const string ReviewMarker = "MIGRATION-SAFETY-REVIEWED:";

    // How far above a flagged call the marker may sit. Comments usually run a few lines; requiring
    // adjacency would be brittle, and allowing it anywhere in the file would let one marker excuse
    // an unrelated operation added later.
    private const int MarkerLookbehindLines = 8;

    // The optional generic argument matters: EF scaffolds AlterColumn as
    // `migrationBuilder.AlterColumn<string>(...)`, so a pattern demanding "(" straight after the
    // name silently matches none of the real ones - which is the single most likely way for this
    // whole check to look like it is working while catching nothing.
    private static readonly Regex OperationPattern = new(
        @"migrationBuilder\.(?<op>DropColumn|DropTable|DropSchema|AlterColumn|RenameColumn|RenameTable|DropIndex|DropForeignKey|DropPrimaryKey|Sql)(?:\s*<[^<>()]*>)?\s*\(",
        RegexOptions.Compiled);

    // Destructive SQL verbs, for the one operation whose effect cannot be read from its name.
    private static readonly Regex DestructiveSqlPattern = new(
        @"\b(DROP|ALTER|DELETE|TRUNCATE|UPDATE)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<MigrationRisk> Inspect(string migrationsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsDirectory);

        if (!Directory.Exists(migrationsDirectory))
        {
            // Not an error: a service may legitimately have no migrations yet, and a check that
            // threw here would fail a suite for the absence of the thing it inspects.
            return [];
        }

        var risks = new List<MigrationRisk>();

        foreach (var path in Directory.EnumerateFiles(migrationsDirectory, "*.cs").OrderBy(p => p))
        {
            var name = Path.GetFileName(path);

            // The designer file and the snapshot are generated descriptions of the model, not
            // operations against a database. Scanning them would report the whole schema as risky.
            if (name.EndsWith("Designer.cs", StringComparison.Ordinal)
                || name.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            {
                continue;
            }

            risks.AddRange(InspectSource(name, File.ReadAllText(path)));
        }

        return risks;
    }

    public static IReadOnlyList<MigrationRisk> InspectSource(string fileName, string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (ExtractUpBody(source) is not { } up)
        {
            return [];
        }

        var risks = new List<MigrationRisk>();

        foreach (Match match in OperationPattern.Matches(up.Body))
        {
            var operation = match.Groups["op"].Value;

            var kind = Classify(operation, up.Body, match.Index);
            if (kind == MigrationRiskKind.None)
            {
                continue;
            }

            if (HasReviewMarkerAbove(up.Body, match.Index))
            {
                continue;
            }

            // Line numbers are relative to the whole file, not the extracted body, so the message
            // points at somewhere a reader can actually go.
            var line = source.Take(up.Offset + match.Index).Count(c => c == '\n') + 1;
            risks.Add(new MigrationRisk(fileName, operation, kind, line, Explain(operation, kind)));
        }

        return risks;
    }

    private static MigrationRiskKind Classify(string operation, string body, int index) => operation switch
    {
        "DropColumn" or "DropTable" or "DropSchema" => MigrationRiskKind.DestroysData,

        // Can narrow a column, change its type, or make it non-nullable over existing rows. EF
        // cannot tell which from the call alone and neither can this.
        "AlterColumn" => MigrationRiskKind.MayDestroyData,

        // No data loss, but it breaks any still-running old version mid-deploy, which is the same
        // outage from the customer's side.
        "RenameColumn" or "RenameTable" => MigrationRiskKind.BreaksRunningCode,

        // Not data loss. Flagged at a lower level because dropping a unique index can let duplicates
        // in before the replacement exists, and dropping a foreign key can orphan rows.
        "DropIndex" or "DropForeignKey" or "DropPrimaryKey" => MigrationRiskKind.RelaxesAConstraint,

        // The one case where the name says nothing. Read the statement instead of refusing all raw
        // SQL - the workspace uses it legitimately for functional indexes EF cannot express, and
        // flagging every CREATE INDEX would train people to acknowledge without reading.
        "Sql" => DestructiveSqlPattern.IsMatch(ExtractFirstArgument(body, index))
            ? MigrationRiskKind.MayDestroyData
            : MigrationRiskKind.None,

        _ => MigrationRiskKind.None,
    };

    private static string Explain(string operation, MigrationRiskKind kind) => kind switch
    {
        MigrationRiskKind.DestroysData =>
            $"{operation} permanently removes customer data that cannot be recovered from the migration.",
        MigrationRiskKind.MayDestroyData =>
            $"{operation} can narrow, re-type or delete existing values depending on what it does.",
        MigrationRiskKind.BreaksRunningCode =>
            $"{operation} breaks any still-running previous version until it is redeployed.",
        MigrationRiskKind.RelaxesAConstraint =>
            $"{operation} removes a guarantee the data currently satisfies; concurrent writes may violate it before a replacement exists.",
        _ => operation,
    };

    // The body of Up(), plus its offset in the file so line numbers stay absolute. Brace-matched
    // rather than regex-delimited, because a migration body contains braces constantly.
    private static (string Body, int Offset)? ExtractUpBody(string source)
    {
        var signature = Regex.Match(
            source, @"protected\s+override\s+void\s+Up\s*\(\s*MigrationBuilder\s+\w+\s*\)\s*\{");
        if (!signature.Success)
        {
            return null;
        }

        var start = signature.Index + signature.Length - 1;
        var depth = 0;

        for (var i = start; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return (source[start..i], start);
                }
            }
        }

        return null;
    }

    private static bool HasReviewMarkerAbove(string body, int index)
    {
        var precedingStart = body.LastIndexOf('\n', Math.Max(index - 1, 0));
        if (precedingStart < 0)
        {
            return false;
        }

        var window = body[..precedingStart];
        var lines = window.Split('\n');
        return lines
            .Skip(Math.Max(0, lines.Length - MarkerLookbehindLines))
            .Any(line => line.Contains(ReviewMarker, StringComparison.Ordinal));
    }

    // The raw-SQL argument, as written. Good enough to spot a destructive verb: migrations in this
    // workspace pass a literal, and a computed one would be unreviewable anyway - in which case
    // finding nothing here and flagging nothing is the wrong answer, so an unparseable argument is
    // treated as destructive by returning the remainder.
    private static string ExtractFirstArgument(string body, int index)
    {
        var open = body.IndexOf('(', index);
        if (open < 0)
        {
            return body[index..];
        }

        var depth = 0;
        for (var i = open; i < body.Length; i++)
        {
            if (body[i] == '(')
            {
                depth++;
            }
            else if (body[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return body[(open + 1)..i];
                }
            }
        }

        return body[open..];
    }

    // A message built for whoever sees the failing test, which may well be somebody who did not
    // write the migration.
    public static string Describe(IReadOnlyList<MigrationRisk> risks)
    {
        ArgumentNullException.ThrowIfNull(risks);

        if (risks.Count == 0)
        {
            return "No risky migration operations found.";
        }

        var message = new StringBuilder()
            .AppendLine($"{risks.Count} migration operation(s) need an explicit, tested upgrade path:")
            .AppendLine();

        foreach (var risk in risks)
        {
            message.AppendLine($"  {risk.File}:{risk.Line}  {risk.Operation}  [{risk.Kind}]");
            message.AppendLine($"      {risk.Detail}");
        }

        message
            .AppendLine()
            .AppendLine("Migrations run against customer databases nobody here can inspect, so the default is")
            .AppendLine("additive-only. If this change is genuinely required and you have a tested path for the")
            .AppendLine("data already in those tables, say so in the migration:")
            .AppendLine()
            .AppendLine($"      // {ReviewMarker} <what happens to existing rows, and how it was tested>")
            .AppendLine()
            .AppendLine($"on one of the {MarkerLookbehindLines} lines above the call. Writing that line is the review;")
            .AppendLine("adding it without one defeats the only check standing between a customer and lost data.");

        return message.ToString();
    }
}

public enum MigrationRiskKind
{
    None = 0,

    // Removes a guarantee the existing data satisfies. No loss, but a window where it can be
    // violated.
    RelaxesAConstraint = 1,

    // Breaks a previous version still serving traffic during a rolling deploy.
    BreaksRunningCode = 2,

    // Could destroy data, depending on specifics this analysis cannot see.
    MayDestroyData = 3,

    // Destroys data, unconditionally.
    DestroysData = 4,
}

public sealed record MigrationRisk(
    string File, string Operation, MigrationRiskKind Kind, int Line, string Detail);
