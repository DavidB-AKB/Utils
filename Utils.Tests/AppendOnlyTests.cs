using Microsoft.EntityFrameworkCore;
using Utils.Data;
using Utils.Data.Auditing;
using Utils.Tests.TestHelpers;

namespace Utils.Tests;

// Verifies the append-only guarantee is actually enforced rather than merely documented. Uses a
// throwaway context that opts in exactly the way a real service does: ConfigureAuditEvents() in
// OnModelCreating, the interceptor in OnConfiguring.
public class AppendOnlyTests
{
    private sealed class TestAuditContext : DbContext
    {
        private readonly string _connectionString;

        public TestAuditContext(string connectionString) => _connectionString = connectionString;

        public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseNpgsql(_connectionString);
            optionsBuilder.AddInterceptors(new AppendOnlySaveChangesInterceptor());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ConfigureAuditEvents();
    }

    // EnsureCreated rather than a migration: this library owns no DbContext, so the context above
    // exists only here and has no migration chain of its own. Idempotent, so the several contexts a
    // single test opens over the same database all go through the same helper.
    private static TestAuditContext NewContext(string connectionString)
    {
        var dbContext = new TestAuditContext(connectionString);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static AuditEvent NewEvent() => new()
    {
        Id = Guid.NewGuid(),
        EventType = "test.happened",
        TargetType = "thing",
        TargetId = "thing-1",
        ActorSubjectType = "user",
        ActorSubjectId = "user-1",
        OccurredAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Insert_IsAllowed()
    {
        await using var dbContext = NewContext(TestPostgres.CreateDatabase());

        dbContext.AuditEvents.Add(NewEvent());
        await dbContext.SaveChangesAsync();

        Assert.Single(dbContext.AuditEvents);
    }

    [Fact]
    public async Task Update_Throws()
    {
        var connectionString = TestPostgres.CreateDatabase();
        await using var seed = NewContext(connectionString);
        seed.AuditEvents.Add(NewEvent());
        await seed.SaveChangesAsync();

        await using var dbContext = NewContext(connectionString);
        var tracked = await dbContext.AuditEvents.SingleAsync();
        dbContext.Entry(tracked).CurrentValues.SetValues(tracked with { EventType = "tampered" });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());
        Assert.Contains("append-only", exception.Message);
    }

    [Fact]
    public async Task Delete_Throws()
    {
        var connectionString = TestPostgres.CreateDatabase();
        await using var seed = NewContext(connectionString);
        seed.AuditEvents.Add(NewEvent());
        await seed.SaveChangesAsync();

        await using var dbContext = NewContext(connectionString);
        dbContext.AuditEvents.Remove(await dbContext.AuditEvents.SingleAsync());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());
        Assert.Contains("append-only", exception.Message);
    }

    // The synchronous path is a separate interceptor override, so it needs its own coverage -
    // an implementation that only guarded SavingChangesAsync would leave a real hole.
    [Fact]
    public async Task Delete_ViaSynchronousSaveChanges_Throws()
    {
        var connectionString = TestPostgres.CreateDatabase();
        await using var seed = NewContext(connectionString);
        seed.AuditEvents.Add(NewEvent());
        await seed.SaveChangesAsync();

        using var dbContext = NewContext(connectionString);
        dbContext.AuditEvents.Remove(dbContext.AuditEvents.Single());

        var exception = Assert.Throws<InvalidOperationException>(() => dbContext.SaveChanges());
        Assert.Contains("append-only", exception.Message);
    }

    // A rejected save must not partially apply: the unrelated insert in the same unit of work is
    // rolled back with the illegal delete, rather than one landing without the other.
    [Fact]
    public async Task RejectedSave_DoesNotPersistOtherChangesInTheSameUnitOfWork()
    {
        var connectionString = TestPostgres.CreateDatabase();
        await using var seed = NewContext(connectionString);
        seed.AuditEvents.Add(NewEvent());
        await seed.SaveChangesAsync();

        await using var dbContext = NewContext(connectionString);
        dbContext.AuditEvents.Add(NewEvent());
        dbContext.AuditEvents.Remove(await dbContext.AuditEvents.FirstAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());

        await using var verify = NewContext(connectionString);
        Assert.Single(verify.AuditEvents);
    }
}
