using LRN.DataLibrary.Abstractions;
using LRN.DataLibrary.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LRN.SharePointUploader.ProcessLogging;

public sealed class LrnProcessLogger
{
    private readonly ILrnLogRepository _repo;
    private readonly ILogger<LrnProcessLogger> _log;
    private readonly LrnStepLogOptions _opt;

    public LrnProcessLogger(ILrnLogRepository repo, IOptions<LrnStepLogOptions> opt, ILogger<LrnProcessLogger> log)
    {
        _repo = repo;
        _log = log;
        _opt = opt.Value;
    }

    // Your exact wrapper style (adjusted only for ILogger)
    public async Task<StepContext> StartStepAsync(
        RunContext runCtx,
        string labName,
        long runId,
        int stepSeq,
        string stepName,
        string stepCategory,
        string sourceSystem,
        string? fileIn,
        string? pathIn,
        long? recordsIn,
        CancellationToken ct)
    {
        var step = await _repo.StartStepAsync(runId, labName, stepSeq, stepName, stepCategory, sourceSystem, fileIn, pathIn, recordsIn, ct);

        _log.LogInformation("Step started. RunID={RunID}, StepSeq={StepSeq}, Step={StepName}", runId, stepSeq, stepName);

        return new StepContext
        {
            StepLogId = step.StepLogId,
            StepSeq = stepSeq,
            StepName = stepName,
            StepCategory = stepCategory,
            StartTimeUtc = step.StartTimeUSST,
            RecordsIn = recordsIn,
            FileNameIn = fileIn,
            PathIn = pathIn
        };
    }

    public async Task CompleteStepAsync(
        long stepLogId,
        string status,
        long? recordsOut,
        string? fileOut,
        string? pathOut,
        string? errorCode,
        string? errorMessage,
        string? errorDetail,
        CancellationToken ct)
    {
        await _repo.CompleteStepAsync(stepLogId, status, recordsOut, fileOut, pathOut, errorCode, errorMessage, errorDetail, ct);
        _log.LogInformation("Step completed. StepLogId={StepLogId}, Status={Status}", stepLogId, status);
    }

    /// <summary>
    /// Convenience: inserts 1 StepLog row for an uploaded file.
    /// StepCategory uses SyncronizeFileType.
    /// </summary>
    public async Task TryLogUploadedFileAsync(
        string runIdToken,
        string labName,
        string syncronizeFileType,
        string localFile,
        string remotePath,
        CancellationToken ct)
    {
        if (!_opt.Enabled)
            return;

        if (!TryParseRunIdToLong(runIdToken, out var runIdLong))
        {
            _log.LogWarning("Unable to parse runId token '{RunIdToken}' to long. Step log skipped for file {File}.", runIdToken, localFile);
            return;
        }

        try
        {
            var runCtx = new RunContext { LabName = labName, SourceSystem = _opt.SourceSystem };

            var step = await StartStepAsync(
                runCtx,
                labName,
                runIdLong,
                _opt.StepSeq,
                _opt.StepName,
                syncronizeFileType,
                _opt.SourceSystem,
                fileIn: Path.GetFileName(localFile),
                pathIn: Path.GetDirectoryName(localFile),
                recordsIn: null,
                ct);

            await CompleteStepAsync(
                step.StepLogId,
                LrnStatuses.Success,
                recordsOut: 1,
                fileOut: Path.GetFileName(localFile),
                pathOut: remotePath,
                errorCode: null,
                errorMessage: null,
                errorDetail: null,
                ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed writing LRN_STEP_LOG for runId={RunIdToken}, file={File}", runIdToken, localFile);

            // Best-effort error log too
            try
            {
                await _repo.LogErrorAsync(new LrnErrorLog
                {
                    RunID = null,
                    StepLogId = null,
                    LabName = labName,
                    SourceSystem = _opt.SourceSystem,
                    ErrorCode = "SP_SYNC",
                    ErrorMessage = ex.Message,
                    ErrorDetail = ex.ToString(),
                    CreatedOnUSST = DateTimeOffset.UtcNow,
                    Host = Environment.MachineName,
                    ExecutedBy = Environment.UserName
                }, ct);
            }
            catch
            {
                // swallow
            }
        }
    }

    private static bool TryParseRunIdToLong(string runIdToken, out long runId)
    {
        runId = 0;
        if (string.IsNullOrWhiteSpace(runIdToken))
            return false;

        // Example token: 20260226R0037 -> 202602260037
        var digits = new string(runIdToken.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out runId) && runId > 0;
    }
}
