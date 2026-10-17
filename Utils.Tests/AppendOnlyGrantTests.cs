using Npgsql;
using Utils.Tests.TestHelpers;

namespace Utils.Tests;

// The database-level half of append-only enforcement, which until now existed only as a paragraph in
// Utils/CLAUDE.md describing SQL that had never been run.
//
// WHY THIS IS WORTH A TEST AT ALL, given it is deployment SQL rather than library code: it is
// documented as "the layer that actually matters", and the C# interceptor it backs up is explicitly
// NOT a security control. So this was the one guarantee in the workspace whose only evidence was
// that somebody had written it down - and the SQL as written could not have worked, because it was
// SQL Server syntax (bracket identifiers, and a DENY that Postgres does not have).
//
// Each test provisions a real database, a real unprivileged login, and then tries to rewrite history
// as that login.
public class AppendOnlyGrantTests
{
    private const string AuditTableDdl = """
        CREATE SCHEMA IF NOT EXISTS users;
        CREATE TABLE users."__EFMigrationsHistory" (
            "MigrationId" character varying(150) NOT NULL PRIMARY KEY,
            "ProductVersion" character varying(32) NOT NULL
        );
        CREATE TABLE users."AuditEvents" (
            "Id" uuid NOT NULL PRIMARY KEY,
            "EventType" text NOT NULL,
            "OccurredAtUtc" timestamptz NOT NULL
        );
        CREATE TABLE users."Things" (
            "Id" uuid NOT NULL PRIMARY KEY,
            "Name" text NOT NULL
        );
        """;

    // Unique per test: Postgres roles are CLUSTER-wide, not per-database, so two tests sharing a
    // container would otherwise fight over one name.
    private sealed record Deployment(string ConnectionString, string RuntimeRole, string MigratorRole);

    private static async Task<Deployment> ProvisionAsync(bool installTriggers = true, bool applyGrants = true)
    {
        var admin = TestPostgres.CreateDatabase();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var runtimeRole = $"rt_{suffix}";
        var migratorRole = $"mg_{suffix}";

        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();

        await ExecuteAsync(connection, $"""
            CREATE ROLE "{migratorRole}" LOGIN PASSWORD 'migrator-pw' NOSUPERUSER NOCREATEDB NOCREATEROLE;
            CREATE ROLE "{runtimeRole}" LOGIN PASSWORD 'runtime-pw' NOSUPERUSER NOCREATEDB NOCREATEROLE;
            """);

        // The migrator owns the schema and tables, exactly as 01-roles.sql intends - which is what
        // stops the runtime role from disabling the triggers.
        await ExecuteAsync(connection, AuditTableDdl);
        await ExecuteAsync(connection, $"""
            ALTER SCHEMA users OWNER TO "{migratorRole}";
            ALTER TABLE users."AuditEvents" OWNER TO "{migratorRole}";
            ALTER TABLE users."Things" OWNER TO "{migratorRole}";
            ALTER TABLE users."__EFMigrationsHistory" OWNER TO "{migratorRole}";
            """);

        if (applyGrants)
        {
            await RunScriptAsync(connection, "02-runtime-grants.sql", runtimeRole, migratorRole);
        }

        if (installTriggers)
        {
            await RunScriptAsync(connection, "03-append-only-audit.sql", runtimeRole, migratorRole);
        }

        return new Deployment(admin, runtimeRole, migratorRole);
    }

    // Runs a deploy script the way psql would, including its :'var' substitutions - Npgsql has no
    // notion of those, so the test performs the same expansion rather than testing a different file
    // than an operator would run.
    private static async Task RunScriptAsync(
        NpgsqlConnection connection, string fileName, string runtimeRole, string migratorRole)
    {
        var sql = await File.ReadAllTextAsync(ScriptPath(fileName));
        // psql meta-commands are stripped: they are interpreted by psql itself and are not SQL, so
        // Npgsql would reject them. Everything that matters to the test is the SQL between them.
        sql = string.Join(
            "\n",
            sql.Split('\n').Where(line => !line.TrimStart().StartsWith("\\", StringComparison.Ordinal)));
        sql = sql
            .Replace(":'runtime_role'", $"'{runtimeRole}'")
            .Replace(":'migrator_role'", $"'{migratorRole}'");
        await ExecuteAsync(connection, sql);
    }

    private static string ScriptPath(string fileName)
    {
        // Walk up to the repo root rather than relying on a copy step, so the test can only ever run
        // the file that is actually committed.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "deploy", "sql")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "deploy", "sql", fileName);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlConnection AsRole(Deployment deployment, string role, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder(deployment.ConnectionString)
        {
            Username = role,
            Password = password,
            Pooling = false,
        };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    private static NpgsqlConnection AsRuntime(Deployment d) => AsRole(d, d.RuntimeRole, "runtime-pw");

    private static async Task<Guid> SeedAuditRowAsync(NpgsqlConnection connection)
    {
        var id = Guid.NewGuid();
        await using var command = new NpgsqlCommand(
            """INSERT INTO users."AuditEvents" ("Id", "EventType", "OccurredAtUtc") VALUES (@id, 'thing.happened', now());""",
            connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    // ---- What the runtime login must be able to do ----

    [Fact]
    public async Task TheRuntimeLoginCanAppendAndReadAuditRows()
    {
        var deployment = await ProvisionAsync();
        await using var connection = AsRuntime(deployment);
        await connection.OpenAsync();

        var id = await SeedAuditRowAsync(connection);

        await using var read = new NpgsqlCommand(
            """SELECT count(*) FROM users."AuditEvents" WHERE "Id" = @id;""", connection);
        read.Parameters.AddWithValue("id", id);
        Assert.Equal(1L, await read.ExecuteScalarAsync());
    }

    // The grants must not be so tight that the application stops working - business tables stay fully
    // writable, and only the audit table is special.
    [Fact]
    public async Task TheRuntimeLoginRetainsFullAccessToBusinessTables()
    {
        var deployment = await ProvisionAsync();
        await using var connection = AsRuntime(deployment);
        await connection.OpenAsync();

        await ExecuteAsync(connection,
            """INSERT INTO users."Things" ("Id", "Name") VALUES (gen_random_uuid(), 'a thing');""");
        await ExecuteAsync(connection, """UPDATE users."Things" SET "Name" = 'renamed';""");
        await ExecuteAsync(connection, """DELETE FROM users."Things";""");
    }

    // ---- What it must not ----

    [Theory]
    [InlineData("""UPDATE users."AuditEvents" SET "EventType" = 'rewritten';""")]
    [InlineData("""DELETE FROM users."AuditEvents";""")]
    [InlineData("""TRUNCATE users."AuditEvents";""")]
    public async Task TheRuntimeLoginCannotRewriteAuditHistory(string statement)
    {
        var deployment = await ProvisionAsync();
        await using var connection = AsRuntime(deployment);
        await connection.OpenAsync();
        await SeedAuditRowAsync(connection);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, statement));

        // 42501 either way, whether it was the grant or the trigger that refused - which is the point
        // of using that SQLSTATE in the trigger: a client cannot tell them apart and should not need to.
        Assert.Equal("42501", exception.SqlState);
    }

    // TRUNCATE is the one people miss. It is a privilege of its own, so withholding DELETE does not
    // cover it, and it fires no row-level triggers - so it needed both a revoke and its own
    // statement-level trigger.
    [Fact]
    public async Task TruncateIsBlockedByTheTriggerEvenForTheTableOwner()
    {
        var deployment = await ProvisionAsync();
        await using var owner = AsRole(deployment, deployment.MigratorRole, "migrator-pw");
        await owner.OpenAsync();
        await SeedAuditRowAsync(owner);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(owner, """TRUNCATE users."AuditEvents";"""));
        Assert.Contains("append-only", exception.MessageText);
    }

    // THE PROPERTY THE GRANTS ALONE CANNOT GIVE, and the reason the triggers exist.
    //
    // Postgres has no DENY - privileges are additive, so a later GRANT simply overrides the revoke in
    // 02. This simulates exactly that: somebody hands the runtime role broader privileges later, as
    // happens when a role is reused or granted a convenience membership. The revoke is gone, and the
    // trigger is what still refuses.
    [Fact]
    public async Task ALaterGrantDefeatsTheRevokeButNotTheTrigger()
    {
        var deployment = await ProvisionAsync();

        await using (var admin = new NpgsqlConnection(deployment.ConnectionString))
        {
            await admin.OpenAsync();
            await ExecuteAsync(admin,
                $"""GRANT UPDATE, DELETE, TRUNCATE ON users."AuditEvents" TO "{deployment.RuntimeRole}";""");
        }

        await using var connection = AsRuntime(deployment);
        await connection.OpenAsync();
        await SeedAuditRowAsync(connection);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(connection, """UPDATE users."AuditEvents" SET "EventType" = 'rewritten';"""));

        // The message proves it was the trigger, not the privilege check - the privilege is now held.
        Assert.Contains("append-only", exception.MessageText);
    }

    // The mirror image, stated as a test so the division of labour cannot be misread as redundancy:
    // with the triggers absent, the grants alone DO still stop the ordinary case.
    [Fact]
    public async Task WithoutTriggersTheGrantsStillStopTheOrdinaryCase()
    {
        var deployment = await ProvisionAsync(installTriggers: false);
        await using var connection = AsRuntime(deployment);
        await connection.OpenAsync();
        await SeedAuditRowAsync(connection);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(connection, """UPDATE users."AuditEvents" SET "EventType" = 'rewritten';"""));
        Assert.Equal("42501", exception.SqlState);

        // ...but not the role-widened one, which is the gap the triggers close.
        await using (var admin = new NpgsqlConnection(deployment.ConnectionString))
        {
            await admin.OpenAsync();
            await ExecuteAsync(admin,
                $"""GRANT UPDATE ON users."AuditEvents" TO "{deployment.RuntimeRole}";""");
        }

        await ExecuteAsync(connection, """UPDATE users."AuditEvents" SET "EventType" = 'rewritten';""");
    }

    // ---- Provisioning behaviour ----

    // Both scripts are re-run after every migration, so running them twice must be a no-op rather
    // than an error - a provisioning step that fails on its second run is one an operator stops
    // running.
    [Fact]
    public async Task BothScriptsAreReRunnable()
    {
        var deployment = await ProvisionAsync();

        await using var admin = new NpgsqlConnection(deployment.ConnectionString);
        await admin.OpenAsync();
        await RunScriptAsync(admin, "02-runtime-grants.sql", deployment.RuntimeRole, deployment.MigratorRole);
        await RunScriptAsync(admin, "03-append-only-audit.sql", deployment.RuntimeRole, deployment.MigratorRole);

        await using var connection = AsRuntime(deployment);
        await connection.OpenAsync();
        await SeedAuditRowAsync(connection);
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(connection, """DELETE FROM users."AuditEvents";"""));
    }

    // Finding no audit tables means the script ran before migrations or against the wrong database.
    // Failing loudly matters because the alternative - printing nothing - looks exactly like success
    // while leaving every audit table unprotected.
    [Fact]
    public async Task TheTriggerScriptRefusesADatabaseWithNoAuditTables()
    {
        var empty = TestPostgres.CreateDatabase();
        await using var connection = new NpgsqlConnection(empty);
        await connection.OpenAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => RunScriptAsync(connection, "03-append-only-audit.sql", "unused", "unused"));
        Assert.Contains("No AuditEvents tables found", exception.MessageText);
    }

    // A new service's schema is found by the presence of its own __EFMigrationsHistory table, which is
    // the workspace's shared-database convention rather than a guess. This is what makes the scripts
    // need no edit when a service is added - and the hand-written list they replaced was already
    // missing two schemas while appearing complete.
    [Fact]
    public async Task GrantsReachASchemaAddedAfterTheFirstProvisioning()
    {
        var deployment = await ProvisionAsync();

        await using (var admin = new NpgsqlConnection(deployment.ConnectionString))
        {
            await admin.OpenAsync();
            await ExecuteAsync(admin, $"""
                CREATE SCHEMA tasks;
                CREATE TABLE tasks."__EFMigrationsHistory" (
                    "MigrationId" character varying(150) NOT NULL PRIMARY KEY,
                    "ProductVersion" character varying(32) NOT NULL);
                CREATE TABLE tasks."WorkItems" ("Id" uuid NOT NULL PRIMARY KEY, "Title" text NOT NULL);
                CREATE TABLE tasks."AuditEvents" (
                    "Id" uuid NOT NULL PRIMARY KEY,
                    "EventType" text NOT NULL,
                    "OccurredAtUtc" timestamptz NOT NULL);
                ALTER SCHEMA tasks OWNER TO "{deployment.MigratorRole}";
                ALTER TABLE tasks."WorkItems" OWNER TO "{deployment.MigratorRole}";
                ALTER TABLE tasks."AuditEvents" OWNER TO "{deployment.MigratorRole}";
                """);

            await RunScriptAsync(admin, "02-runtime-grants.sql", deployment.RuntimeRole, deployment.MigratorRole);
            await RunScriptAsync(admin, "03-append-only-audit.sql", deployment.RuntimeRole, deployment.MigratorRole);
        }

        await using var connection = AsRuntime(deployment);
        await connection.OpenAsync();

        await ExecuteAsync(connection,
            """INSERT INTO tasks."WorkItems" ("Id", "Title") VALUES (gen_random_uuid(), 'ship it');""");
        await ExecuteAsync(connection,
            """INSERT INTO tasks."AuditEvents" ("Id", "EventType", "OccurredAtUtc") VALUES (gen_random_uuid(), 'x', now());""");
        await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(connection, """DELETE FROM tasks."AuditEvents";"""));
    }
}
