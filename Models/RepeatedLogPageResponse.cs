namespace BoldLogValidator.Models;

public class RepeatedLogPageResponse
{
    public List<GroupedLogSummary> Entries { get; set; } = [];

    public int ReturnedCount { get; set; }

    public int TotalCount { get; set; }

    public bool HasMore { get; set; }
}
