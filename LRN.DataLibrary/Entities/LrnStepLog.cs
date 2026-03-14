namespace LRN.DataLibrary.Entities;

public sealed class LrnStepLog
{
    public long StepLogId { get; set; }

    public long RunID { get; set; }
    public string LabName { get; set; } = "";

    public int StepSeq { get; set; }
    public string StepName { get; set; } = "";
    public string StepCategory { get; set; } = "";

    public string SourceSystem { get; set; } = "";

    public DateTimeOffset StartTimeUSST { get; set; }
    public DateTimeOffset? EndTimeUSST { get; set; }

    public string Status { get; set; } = LrnStatuses.Pending;

    public long? RecordsIn { get; set; }
    public long? RecordsOut { get; set; }

    public string? FileNameIn { get; set; }
    public string? PathIn { get; set; }

    public string? FileNameOut { get; set; }
    public string? PathOut { get; set; }

    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorDetail { get; set; }

    public string? Host { get; set; }
    public string? ExecutedBy { get; set; }
    public string? ModuleVersion { get; set; }
}
