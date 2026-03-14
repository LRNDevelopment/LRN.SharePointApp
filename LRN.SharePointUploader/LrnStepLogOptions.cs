public sealed class LrnStepLogOptions
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// If set, this is used as ConnectionStrings:LrnLogDb (preferred).
    /// If empty, the app can still use ConnectionStrings:LrnLogDb directly.
    /// </summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// Written into StepLogs.SourceSystem (e.g., "SharePointUploader").
    /// </summary>
    public string SourceSystem { get; set; } = "SharePointUploader";

    /// <summary>
    /// Default step name used for each uploaded file.
    /// </summary>
    public string StepName { get; set; } = "SharePointSync";

    /// <summary>
    /// Default step sequence.
    /// </summary>
    public int StepSeq { get; set; } = 900;
}
