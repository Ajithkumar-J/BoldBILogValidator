using Microsoft.AspNetCore.Http;

namespace BoldLogValidator.Models;

public class AnalysisFilterInput
{
    public string? UploadSessionId { get; set; }

    public bool UseLocalLogPath { get; set; } = true;

    public string LocalLogPath { get; set; } = @"C:\BoldServices\app_data\logs";

    public string? BrowserTimeZone { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    public string AnalysisMode { get; set; } = "overall";

    public string? SpecificService { get; set; }

    public string? CorrelationId { get; set; }

    public string? TraceId { get; set; }

    public string? SpanId { get; set; }

    public string? Keyword { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    public bool IncludeErrors { get; set; } = true;

    public bool IncludeDebugInfo { get; set; } = true;

    public bool EnableConcurrentInsights { get; set; } = true;

    public List<IFormFile> LogFiles { get; set; } = [];

    public IFormFile? HarFile { get; set; }
}
