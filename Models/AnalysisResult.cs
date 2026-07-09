namespace BoldLogValidator.Models;

public class AnalysisResult
{
    public AnalysisFilterInput Filter { get; set; } = new();

    public string? UploadSessionId { get; set; }

    public int UploadedFileCount { get; set; }

    public int ParsedEntryCount { get; set; }

    public int FilteredEntryCount { get; set; }

    public int ErrorCount { get; set; }

    public int FilteredErrorCount { get; set; }

    public int ServiceCount { get; set; }

    public DateTime? RangeStart { get; set; }

    public DateTime? RangeEnd { get; set; }

    public string BrowserTimeZone { get; set; } = "UTC";

    public List<string> AppliedIdentifiers { get; set; } = [];

    public List<string> Notes { get; set; } = [];

    public List<ServiceSummary> ServiceSummaries { get; set; } = [];

    public List<ConcurrentInsight> ConcurrentInsights { get; set; } = [];

    public List<SignatureSummary> SignatureSummaries { get; set; } = [];

    public List<GroupedLogSummary> GroupedLogSummaries { get; set; } = [];

    public List<HarApiRecord> HarApis { get; set; } = [];

    public List<ParsedLogEntry> HighlightedEntries { get; set; } = [];
}

public class ServiceSummary
{
    public string Service { get; set; } = string.Empty;

    public int TotalEntries { get; set; }

    public int ErrorCount { get; set; }

    public int WarningCount { get; set; }

    public int DistinctCorrelationCount { get; set; }

    public string TopErrorSummary { get; set; } = "No errors found";

    public string TopErrorMessage { get; set; } = string.Empty;

    public int TopErrorCount { get; set; }
}

public class ConcurrentInsight
{
    public string Kind { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public int OccurrenceCount { get; set; }

    public string Services { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string ExampleMessage { get; set; } = string.Empty;
}

public class SignatureSummary
{
    public string Signature { get; set; } = string.Empty;

    public int Count { get; set; }

    public string Services { get; set; } = string.Empty;

    public string ExampleMessage { get; set; } = string.Empty;
}

public class GroupedLogSummary
{
    public string Service { get; set; } = string.Empty;

    public string Signature { get; set; } = string.Empty;

    public int Count { get; set; }

    public string ExampleMessage { get; set; } = string.Empty;

    public string Severity { get; set; } = string.Empty;
}
