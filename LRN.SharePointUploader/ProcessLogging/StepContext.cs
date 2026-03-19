namespace LRN.SharePointUploader.ProcessLogging;

public sealed class StepContext
{
    public long StepLogId { get; set; }
    public int StepSeq { get; set; }
    public string StepName { get; set; } = "";
    public string StepCategory { get; set; } = "";
    public DateTimeOffset StartTimeUtc { get; set; }

    public long? RecordsIn { get; set; }
    public string? FileNameIn { get; set; }
    public string? PathIn { get; set; }
}
