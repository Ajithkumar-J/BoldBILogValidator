namespace BoldLogValidator.Models;

public class TimelinePageResponse
{
    public List<ParsedLogEntry> Entries { get; set; } = [];

    public int ReturnedCount { get; set; }

    public int TotalCount { get; set; }

    public bool HasMore { get; set; }
}
