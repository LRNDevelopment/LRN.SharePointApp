using LRN.DataLibrary.Entities;

namespace LRN.DataLibrary.Abstractions;

public interface ILrnLogRepository
{
    Task<LrnRunLog> CreateRunAsync(int labId, string labName, string sourceSystem, CancellationToken ct);
    Task<LrnRunLog?> GetLatestRunAsync(int labId, CancellationToken ct);
    Task UpdateRunAsync(LrnRunLog run, CancellationToken ct);

    Task<LrnStepLog> StartStepAsync(
        long runId,
        string labName,
        int stepSeq,
        string stepName,
        string stepCategory,
        string sourceSystem,
        string? fileIn = null,
        string? pathIn = null,
        long? recordsIn = null,
        CancellationToken ct = default);

    Task CompleteStepAsync(
        long stepLogId,
        string status,
        long? recordsOut,
        string? fileOut,
        string? pathOut,
        string? errorCode,
        string? errorMessage,
        string? errorDetail,
        CancellationToken ct);

    Task LogErrorAsync(LrnErrorLog error, CancellationToken ct);
}
