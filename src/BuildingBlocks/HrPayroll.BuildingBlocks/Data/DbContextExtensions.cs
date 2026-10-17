using Microsoft.EntityFrameworkCore;

namespace HrPayroll.BuildingBlocks.Data;

public static class DbContextExtensions
{
    /// <summary>
    /// Runs <paramref name="work"/> in a transaction that is compatible with the SQL Server
    /// retry-on-failure execution strategy (the whole unit is retried on transient errors).
    /// </summary>
    public static Task<T> InTransactionAsync<T>(this DbContext db, Func<CancellationToken, Task<T>> work, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            db.ChangeTracker.Clear();   // a retry must not see entities from the failed attempt
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var result = await work(token);
            await tx.CommitAsync(token);
            return result;
        }, ct);
}
