namespace BoldLogValidator.Models;

public class RepeatedLogPageRequest
{
    public string AnalysisSessionId { get; set; } = string.Empty;

    public int Skip { get; set; }

    public string? Service { get; set; }
}
