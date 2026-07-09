namespace BoldLogValidator.Models;

public class ParsedLogEntry
{
    public DateTime Timestamp { get; set; }

    public string Service { get; set; } = "unknown";

    public string RelativePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Severity { get; set; } = "Info";

    public string? CorrelationId { get; set; }

    public string? TraceId { get; set; }

    public string? SpanId { get; set; }

    public string? RequestId { get; set; }

    public string? Operation { get; set; }

    public string? Stage { get; set; }

    public string Message { get; set; } = string.Empty;

    public string Signature { get; set; } = string.Empty;

    public string RawLine { get; set; } = string.Empty;

    public int LineNumber { get; set; }
}
