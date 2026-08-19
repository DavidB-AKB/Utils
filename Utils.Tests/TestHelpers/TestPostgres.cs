using Npgsql;
using Testcontainers.PostgreSql;

namespace Utils.Tests.TestHelpers;

// One real Postgres container for the whole test assembly, replacing EF Core's InMemory provider.
//
// InMemory is not a database - it enforces no unique indexes, no foreign keys, no column lengths and
// no NOT NULL, and it does not enforce concurrency tokens across separate contexts at all. That last
// one is why this repo in particular needed the switch: the leased queue's entire safety property is
// that a contended claim is REJECTED, and InMemory let both claims through, so the guard was
// unverifiable by construction. See LeasedQueueTests' contended-claim test.
//
// SQLite was tried first and rejected: it cannot ORDER BY either a DateTimeOffset or a Guid, which
// is exactly what every cursor-paginated query in this workspace sorts on. See the top-level
// CLAUDE.md's "Test database provider" bullet.
//
// No template database here, unlike the service repos' otherwise-identical helpers: this library
// owns no DbContext and therefore no migrations. The contexts under test are declared by the test
// classes themselves and built with EnsureCreated, so there is no migration chain to pre-apply and
// nothing for a template to save.
public static class TestPostgres
{
    // Pinned rather than :latest so a run can't start failing because a new major Postgres shipped.
    private static readonly PostgreSqlContainer Container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _started;
    private static int _databaseCounter;

    // An empty database per caller. The caller creates its own schema with EnsureCreated; sharing
    // the returned connection string across several contexts (as the append-only tests do, to prove
    // a rejected save left nothing behind) is exactly the point of handing back a string rather than
    // a context.
    //
    // Sync deliberately: the test helpers that call it are synchronous, and keeping it that way
    // meant no test body had to change shape.
    public static string CreateDatabase()
    {
        EnsureStartedAsync().GetAwaiter().GetResult();

        var name = $"test_{Interlocked.Increment(ref _databaseCounter)}";
        ExecAsync($"CREATE DATABASE {name};").GetAwaiter().GetResult();

        return new NpgsqlConnectionStringBuilder(Container.GetConnectionString())
        {
            Database = name,
        }.ConnectionString;
    }

    private static async Task EnsureStartedAsync()
    {
        if (_started)
        {
            return;
        }

        await Gate.WaitAsync();
        try
        {
            if (_started)
            {
                return;
            }

            await Container.StartAsync();
            _started = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task ExecAsync(string sql)
    {
        // Runs against the container's default database - CREATE DATABASE cannot be executed against
        // the database being created, nor inside a transaction.
        var result = await Container.ExecScriptAsync(sql);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Postgres setup failed for \"{sql}\": {result.Stderr}");
        }
    }
}
