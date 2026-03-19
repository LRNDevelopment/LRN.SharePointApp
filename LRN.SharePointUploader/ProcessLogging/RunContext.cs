namespace LRN.SharePointUploader.ProcessLogging;

public sealed class RunContext
{
    public int? LabId { get; set; }
    public string? LabName { get; set; }
    public string SourceSystem { get; set; } = "SharePointUploader";
}
