namespace BoldLogValidator.Models;

public class TimelinePageRequest
{
    public string AnalysisSessionId { get; set; } = string.Empty;

    public int Skip { get; set; }

    public string? TimelineService { get; set; }

    public string TimelineSortOrder { get; set; } = "desc";
}
