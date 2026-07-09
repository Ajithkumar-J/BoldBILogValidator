namespace BoldLogValidator.Models;

public class HarApiRecord
{
    public DateTime? StartedAt { get; set; }

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public int? StatusCode { get; set; }

    public string? CorrelationId { get; set; }

    public string? TraceId { get; set; }

    public string? SpanId { get; set; }

    public string? RequestId { get; set; }
}
