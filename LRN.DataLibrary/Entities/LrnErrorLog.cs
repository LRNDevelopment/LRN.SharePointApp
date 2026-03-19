namespace LRN.DataLibrary.Entities;

public sealed class LrnErrorLog
{
    public long ErrorLogId { get; set; }

    public long? RunID { get; set; }
    public long? StepLogId { get; set; }

    public string? LabName { get; set; }
    public string? SourceSystem { get; set; }

    public string? ErrorCode { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string? ErrorDetail { get; set; }

    public DateTimeOffset CreatedOnUSST { get; set; }
    public string? Host { get; set; }
    public string? ExecutedBy { get; set; }
}
