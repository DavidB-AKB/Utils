using Microsoft.EntityFrameworkCore;
using Utils.Data;
using Utils.Data.Auditing;

namespace Utils.Tests;

// Verifies the append-only guarantee is actually enforced rather than merely documented. Uses a
// throwaway context that opts in exactly the way a real service does: ConfigureAuditEvents() in
// OnModelCreating, the interceptor in OnConfiguring.
public class AppendOnlyTests
{
    private sealed class TestAuditContext : DbContext
    {
        private readonly string _databaseName;

        public TestAuditContext(string databaseName) => _databaseName = databaseName;

        public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseInMemoryDatabase(_databaseName);
            optionsBuilder.AddInterceptors(new AppendOnlySaveChangesInterceptor());
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ConfigureAuditEvents();
    }

    private static TestAuditContext NewContext(string databaseName) => new(databaseName);

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
        await using var dbContext = NewContext(Guid.NewGuid().ToString());

        dbContext.AuditEvents.Add(NewEvent());
        await dbContext.SaveChangesAsync();

        Assert.Single(dbContext.AuditEvents);
    }

    [Fact]
    public async Task Update_Throws()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var seed = NewContext(databaseName);
        seed.AuditEvents.Add(NewEvent());
        await seed.SaveChangesAsync();

        await using var dbContext = NewContext(databaseName);
        var tracked = await dbContext.AuditEvents.SingleAsync();
        dbContext.Entry(tracked).CurrentValues.SetValues(tracked with { EventType = "tampered" });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());
        Assert.Contains("append-only", exception.Message);
    }

    [Fact]
    public async Task Delete_Throws()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var seed = NewContext(databaseName);
        seed.AuditEvents.Add(NewEvent());
        await seed.SaveChangesAsync();

        await using var dbContext = NewContext(databaseName);
        dbContext.AuditEvents.Remove(await dbContext.AuditEvents.SingleAsync());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());
        Assert.Contains("append-only", exception.Message);
    }

    // The synchronous path is a separate interceptor override, so it needs its own coverage -
    // an implementation that only guarded SavingChangesAsync would leave a real hole.
    [Fact]
    public async Task Delete_ViaSynchronousSaveChanges_Throws()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var seed = NewContext(databaseName);
        seed.AuditEvents.Add(NewEvent());
        await seed.SaveChangesAsync();

        using var dbContext = NewContext(databaseName);
        dbContext.AuditEvents.Remove(dbContext.AuditEvents.Single());

        var exception = Assert.Throws<InvalidOperationException>(() => dbContext.SaveChanges());
        Assert.Contains("append-only", exception.Message);
    }

    // A rejected save must not partially apply: the unrelated insert in the same unit of work is
    // rolled back with the illegal delete, rather than one landing without the other.
    [Fact]
    public async Task RejectedSave_DoesNotPersistOtherChangesInTheSameUnitOfWork()
    {
        var databaseName = Guid.NewGuid().ToString();
        await using var seed = NewContext(databaseName);
        seed.AuditEvents.Add(NewEvent());
        await seed.SaveChangesAsync();

        await using var dbContext = NewContext(databaseName);
        dbContext.AuditEvents.Add(NewEvent());
        dbContext.AuditEvents.Remove(await dbContext.AuditEvents.FirstAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());

        await using var verify = NewContext(databaseName);
        Assert.Single(verify.AuditEvents);
    }
}
