namespace LRN.DataLibrary.Entities;

public sealed class LrnRunLog
{
    public long RunID { get; set; }

    public int LabId { get; set; }
    public string LabName { get; set; } = "";
    public string SourceSystem { get; set; } = "";

    public DateTimeOffset StartTimeUSST { get; set; }
    public DateTimeOffset? EndTimeUSST { get; set; }
    public DateTimeOffset UpdatedOn { get; set; }

    public string OverallStatus { get; set; } = LrnStatuses.Pending;

    public bool LatestMasterFileFound { get; set; }

    public string MandatoryColumnCheck { get; set; } = LrnStatuses.Pending;
    public string SplitOutputWrittenToSharePoint { get; set; } = LrnStatuses.Pending;

    public string PayerPolicyValidationStatus { get; set; } = LrnStatuses.Pending;
    public string CodingValidationStatus { get; set; } = LrnStatuses.Pending;
    public string AveragesProcessStatus { get; set; } = LrnStatuses.Pending;

    public string OutputsCopiedToSharePoint { get; set; } = LrnStatuses.Pending;
}
