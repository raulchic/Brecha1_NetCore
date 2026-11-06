using Microsoft.EntityFrameworkCore;
using WebApi.Models;

namespace WebApi.Infrastructure;

public class BatchRepository(BatchDbContext dbContext) : IBatchRepository
{
    public async Task<(BatchFileRecord Batch, bool IsNew)> CreateAsync(
        BatchFileRecord batch,
        CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.Batches.AsNoTracking()
            .SingleOrDefaultAsync(item => item.IdempotencyKey == batch.IdempotencyKey, cancellationToken);

        if (existing is not null)
            return (existing, false);

        dbContext.Batches.Add(batch);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return (batch, true);
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(batch).State = EntityState.Detached;
            existing = await dbContext.Batches.AsNoTracking()
                .SingleOrDefaultAsync(item => item.IdempotencyKey == batch.IdempotencyKey, cancellationToken);

            if (existing is null)
                throw;

            return (existing, false);
        }
    }

    public Task<BatchFileRecord?> GetByBatchIdAsync(
        string batchId,
        CancellationToken cancellationToken = default)
        => dbContext.Batches.AsNoTracking()
            .SingleOrDefaultAsync(batch => batch.BatchId == batchId, cancellationToken);

    public async Task UpdateStatusAsync(
        string batchId,
        BatchStatusEnum status,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        var batch = await dbContext.Batches
            .SingleOrDefaultAsync(item => item.BatchId == batchId, cancellationToken);

        if (batch is null)
            return;

        batch.Status = status.ToString();
        batch.UpdatedAt = DateTimeOffset.UtcNow;
        batch.ErrorMessage = errorMessage;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveLineAsync(
        string batchId,
        int lineNumber,
        string content,
        bool succeeded,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        dbContext.BatchLines.Add(new BatchLineRecord
        {
            BatchId = batchId,
            LineNumber = lineNumber,
            Content = content,
            Succeeded = succeeded,
            ErrorMessage = errorMessage
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}