using WebApi.Models;

namespace WebApi.Infrastructure;

public interface IBatchRepository
{
    Task<(BatchFileRecord Batch, bool IsNew)> CreateAsync(
        BatchFileRecord batch,
        CancellationToken cancellationToken = default);

    Task<BatchFileRecord?> GetByBatchIdAsync(
        string batchId,
        CancellationToken cancellationToken = default);

    Task UpdateStatusAsync(
        string batchId,
        BatchStatusEnum status,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);

    Task SaveLineAsync(
        string batchId,
        int lineNumber,
        string content,
        bool succeeded,
        string? errorMessage = null,
        CancellationToken cancellationToken = default);
}