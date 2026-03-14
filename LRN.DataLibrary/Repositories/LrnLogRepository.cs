using LRN.DataLibrary.Abstractions;
using LRN.DataLibrary.Db;
using LRN.DataLibrary.Entities;
using Microsoft.EntityFrameworkCore;

namespace LRN.DataLibrary.Repositories;

public class LrnLogRepository : ILrnLogRepository
{
    private readonly LrnLogDbContext _db;

    public LrnLogRepository(LrnLogDbContext db)
    {
        _db = db;
    }

    public async Task<LrnRunLog> CreateRunAsync(int labId, string labName, string sourceSystem, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var run = new LrnRunLog
        {
            LabId = labId,
            LabName = labName,
            SourceSystem = sourceSystem,
            StartTimeUSST = now,
            UpdatedOn = now,
            OverallStatus = LrnStatuses.Pending,
            LatestMasterFileFound = false,
            MandatoryColumnCheck = LrnStatuses.Pending,
            SplitOutputWrittenToSharePoint = LrnStatuses.Pending,
            PayerPolicyValidationStatus = LrnStatuses.Pending,
            CodingValidationStatus = LrnStatuses.Pending,
            AveragesProcessStatus = LrnStatuses.Pending,
            OutputsCopiedToSharePoint = LrnStatuses.Pending,
        };

        _db.RunLogs.Add(run);
        await _db.SaveChangesAsync(ct);
        return run;
    }

    public Task<LrnRunLog?> GetLatestRunAsync(int labId, CancellationToken ct)
    {
        return _db.RunLogs
            .AsNoTracking()
            .Where(x => x.LabId == labId)
            .OrderByDescending(x => x.RunID)
            .FirstOrDefaultAsync(ct);
    }

    public async Task UpdateRunAsync(LrnRunLog run, CancellationToken ct)
    {
        run.UpdatedOn = DateTimeOffset.UtcNow;
        _db.RunLogs.Update(run);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<LrnStepLog> StartStepAsync(long runId, string labName, int stepSeq, string stepName, string stepCategory,
        string sourceSystem, string? fileIn = null, string? pathIn = null, long? recordsIn = null, CancellationToken ct = default)
    {
        var step = new LrnStepLog
        {
            RunID = runId,
            LabName = labName,
            StepSeq = stepSeq,
            StepName = stepName,
            StepCategory = stepCategory,
            SourceSystem = sourceSystem,
            StartTimeUSST = DateTimeOffset.UtcNow,
            Status = LrnStatuses.Pending,
            RecordsIn = recordsIn,
            FileNameIn = fileIn,
            PathIn = pathIn,
            Host = Environment.MachineName,
            ExecutedBy = Environment.UserName,
            ModuleVersion = typeof(LrnLogRepository).Assembly.GetName().Version?.ToString()
        };

        _db.StepLogs.Add(step);
        await _db.SaveChangesAsync(ct);
        return step;
    }

    public async Task CompleteStepAsync(long stepLogId, string status, long? recordsOut, string? fileOut, string? pathOut,
        string? errorCode, string? errorMessage, string? errorDetail, CancellationToken ct)
    {
        var step = await _db.StepLogs.FirstAsync(x => x.StepLogId == stepLogId, ct);
        step.EndTimeUSST = DateTimeOffset.UtcNow;
        step.Status = status;
        step.RecordsOut = recordsOut;
        step.FileNameOut = fileOut;
        step.PathOut = pathOut;
        step.ErrorCode = errorCode;
        step.ErrorMessage = errorMessage;
        step.ErrorDetail = errorDetail;
        await _db.SaveChangesAsync(ct);
    }

    public async Task LogErrorAsync(LrnErrorLog error, CancellationToken ct)
    {
        _db.ErrorLogs.Add(error);
        await _db.SaveChangesAsync(ct);
    }
}
