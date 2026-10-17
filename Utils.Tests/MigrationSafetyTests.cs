using Utils.Migrations;

namespace Utils.Tests;

// Pure source analysis, so no container and no database.
public class MigrationSafetyTests
{
    private static string Migration(string up, string down = "") => $$"""
        using Microsoft.EntityFrameworkCore.Migrations;

        public partial class Example : Migration
        {
            protected override void Up(MigrationBuilder migrationBuilder)
            {
        {{up}}
            }

            protected override void Down(MigrationBuilder migrationBuilder)
            {
        {{down}}
            }
        }
        """;

    // THE DISTINCTION THAT MAKES THIS WORTH WRITING RATHER THAN GREPPING. Down() is destructive by
    // definition - undoing a migration is what it is for - and every InitialCreate in this workspace
    // drops every table it created. A naive grep reports ~70 operations across these repos; exactly
    // one of them is in an Up().
    [Fact]
    public void OperationsInDownAreIgnored()
    {
        var source = Migration(
            up: """        migrationBuilder.CreateTable(name: "Things");""",
            down: """        migrationBuilder.DropTable(name: "Things");""");

        Assert.Empty(MigrationSafety.InspectSource("Example.cs", source));
    }

    [Theory]
    [InlineData("DropColumn", MigrationRiskKind.DestroysData)]
    [InlineData("DropTable", MigrationRiskKind.DestroysData)]
    [InlineData("DropSchema", MigrationRiskKind.DestroysData)]
    [InlineData("AlterColumn", MigrationRiskKind.MayDestroyData)]
    [InlineData("RenameColumn", MigrationRiskKind.BreaksRunningCode)]
    [InlineData("RenameTable", MigrationRiskKind.BreaksRunningCode)]
    [InlineData("DropIndex", MigrationRiskKind.RelaxesAConstraint)]
    [InlineData("DropForeignKey", MigrationRiskKind.RelaxesAConstraint)]
    public void DestructiveOperationsInUpAreClassified(string operation, MigrationRiskKind expected)
    {
        var source = Migration($"""        migrationBuilder.{operation}(name: "X", table: "Y");""");

        var risk = Assert.Single(MigrationSafety.InspectSource("Example.cs", source));
        Assert.Equal(operation, risk.Operation);
        Assert.Equal(expected, risk.Kind);
    }

    [Fact]
    public void AdditiveOperationsAreNotFlagged()
    {
        var source = Migration("""
                    migrationBuilder.CreateTable(name: "Things");
                    migrationBuilder.AddColumn<string>(name: "Note", table: "Things", nullable: true);
                    migrationBuilder.CreateIndex(name: "IX_Things_Note", table: "Things", column: "Note");
            """);

        Assert.Empty(MigrationSafety.InspectSource("Example.cs", source));
    }

    // Raw SQL is the one operation whose effect cannot be read from its name, so the statement is
    // read instead. Refusing ALL raw SQL would be noise here - the workspace uses it legitimately for
    // functional indexes EF cannot express - and noise trains people to acknowledge without reading.
    [Fact]
    public void AdditiveRawSqlIsNotFlagged()
    {
        var source = Migration("""
                    migrationBuilder.Sql("CREATE UNIQUE INDEX \"IX_Workflows_Key_Lower\" ON tasks.\"Workflows\" (lower(\"Key\"));");
            """);

        Assert.Empty(MigrationSafety.InspectSource("Example.cs", source));
    }

    [Theory]
    [InlineData("DROP INDEX IF EXISTS tasks.\\\"IX_Thing\\\";")]
    [InlineData("UPDATE users.\\\"Users\\\" SET \\\"Name\\\" = 'x';")]
    [InlineData("DELETE FROM leads.\\\"Leads\\\" WHERE 1=1;")]
    [InlineData("ALTER TABLE comments.\\\"Comments\\\" DROP COLUMN \\\"Body\\\";")]
    public void DestructiveRawSqlIsFlagged(string statement)
    {
        var source = Migration($"""        migrationBuilder.Sql("{statement}");""");

        var risk = Assert.Single(MigrationSafety.InspectSource("Example.cs", source));
        Assert.Equal(MigrationRiskKind.MayDestroyData, risk.Kind);
    }

    // ---- The acknowledgement ----

    [Fact]
    public void AReviewedOperationIsAccepted()
    {
        var source = Migration($"""
                    // {MigrationSafety.ReviewMarker} Column was added last release and is empty in
                    // every deployment; verified against a restored backup.
                    migrationBuilder.DropColumn(name: "Unused", table: "Things");
            """);

        Assert.Empty(MigrationSafety.InspectSource("Example.cs", source));
    }

    // One marker must not excuse an unrelated operation added underneath it later - which is exactly
    // how this would rot, since the second author opens a file that already has a marker in it.
    [Fact]
    public void AMarkerDoesNotExcuseAnOperationFarBelowIt()
    {
        var up = string.Join("\n", new[]
            {
                $"            // {MigrationSafety.ReviewMarker} this covers the first one only.",
                "            migrationBuilder.DropColumn(name: \"First\", table: \"Things\");",
            }
            .Concat(Enumerable.Repeat("            // ...", 12))
            .Append("            migrationBuilder.DropColumn(name: \"Second\", table: \"Things\");"));

        var source = Migration(up);

        // Exactly one survives: the far one. The near one is covered by the marker above it.
        var risk = Assert.Single(MigrationSafety.InspectSource("Example.cs", source));
        Assert.Equal("DropColumn", risk.Operation);
        Assert.Contains("Second", source.Split('\n')[risk.Line - 1]);
    }

    [Fact]
    public void EveryRiskIsReportedNotJustTheFirst()
    {
        var source = Migration("""
                    migrationBuilder.DropColumn(name: "A", table: "Things");
                    migrationBuilder.DropTable(name: "Other");
                    migrationBuilder.AlterColumn<string>(name: "B", table: "Things");
            """);

        Assert.Equal(3, MigrationSafety.InspectSource("Example.cs", source).Count);
    }

    // The message is read by whoever sees the failing test, who may not have written the migration -
    // so it has to say what to do, not just what is wrong.
    [Fact]
    public void TheDescriptionNamesTheFileLineAndTheWayToAcceptIt()
    {
        var source = Migration("""        migrationBuilder.DropColumn(name: "A", table: "Things");""");

        var description = MigrationSafety.Describe(MigrationSafety.InspectSource("Example.cs", source));

        Assert.Contains("Example.cs:", description);
        Assert.Contains(MigrationSafety.ReviewMarker, description);
        Assert.Contains("additive-only", description);
    }

    [Fact]
    public void LineNumbersPointAtTheRealLineInTheFile()
    {
        var source = Migration("""        migrationBuilder.DropColumn(name: "A", table: "Things");""");

        var risk = Assert.Single(MigrationSafety.InspectSource("Example.cs", source));
        var lines = source.Split('\n');
        Assert.Contains("DropColumn", lines[risk.Line - 1]);
    }

    // ---- Directory scanning ----

    [Fact]
    public void GeneratedDesignerAndSnapshotFilesAreSkipped()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"migsafety-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var risky = Migration("""        migrationBuilder.DropTable(name: "Things");""");
            File.WriteAllText(Path.Combine(directory, "20260101_X.Designer.cs"), risky);
            File.WriteAllText(Path.Combine(directory, "AppDbContextModelSnapshot.cs"), risky);

            Assert.Empty(MigrationSafety.Inspect(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // A service with no migrations yet must not fail its suite for the absence of the thing being
    // inspected.
    [Fact]
    public void AnAbsentMigrationsDirectoryIsNotAnError()
    {
        Assert.Empty(MigrationSafety.Inspect(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}")));
    }
}
