using Microsoft.AspNetCore.Http;

namespace BoldLogValidator.Models;

public class HarValidationPageViewModel
{
    public HarValidationFilterInput Filter { get; set; } = new();

    public HarValidationResult? Result { get; set; }
}

public class HarValidationFilterInput
{
    public string? BrowserTimeZone { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }

    public string? Keyword { get; set; }

    public string ApiCategory { get; set; } = "all";

    public string Method { get; set; } = "all";

    public string StatusFamily { get; set; } = "all";

    public string? CorrelationId { get; set; }

    public string? TraceId { get; set; }

    public string? SelectedRequestKey { get; set; }

    public IFormFile? HarFile { get; set; }
}

public class HarValidationResult
{
    public HarValidationFilterInput Filter { get; set; } = new();

    public string BrowserTimeZone { get; set; } = "UTC";

    public string? ActiveHarFileName { get; set; }

    public string? ActiveHarSourceLabel { get; set; }

    public string? DashboardPath { get; set; }

    public string? EnvironmentLabel { get; set; }

    public int TotalApis { get; set; }

    public int DistinctEndpoints { get; set; }

    public int ErrorApis { get; set; }

    public int SlowApis { get; set; }

    public int LoadDashboardHits { get; set; }

    public double AverageResponseTimeMs { get; set; }

    public List<string> Notes { get; set; } = [];

    public List<string> StatusChips { get; set; } = [];

    public List<string> AppliedFilters { get; set; } = [];

    public List<string> CategoryOptions { get; set; } = [];

    public List<HarValidationApiItem> FilteredApis { get; set; } = [];

    public HarValidationApiItem? SelectedApi { get; set; }

    public List<HarKeyValueItem> SelectedRequestHeaders { get; set; } = [];

    public List<HarKeyValueItem> SelectedResponseHeaders { get; set; } = [];

    public List<HarKeyValueItem> SelectedQueryParameters { get; set; } = [];

    public string? SelectedPayloadText { get; set; }

    public JsonTreeNode? SelectedPayloadTree { get; set; }

    public bool SelectedPayloadWasNestedDecoded { get; set; }

    public string? SelectedResponseText { get; set; }

    public JsonTreeNode? SelectedResponseTree { get; set; }

    public bool SelectedResponseWasNestedDecoded { get; set; }
}

public class HarRequestDetailsResult
{
    public string? SelectedRequestKey { get; set; }

    public HarValidationApiItem? SelectedApi { get; set; }

    public List<HarKeyValueItem> SelectedRequestHeaders { get; set; } = [];

    public List<HarKeyValueItem> SelectedResponseHeaders { get; set; } = [];

    public List<HarKeyValueItem> SelectedQueryParameters { get; set; } = [];

    public string? SelectedPayloadText { get; set; }

    public JsonTreeNode? SelectedPayloadTree { get; set; }

    public bool SelectedPayloadWasNestedDecoded { get; set; }

    public string? SelectedResponseText { get; set; }

    public JsonTreeNode? SelectedResponseTree { get; set; }

    public bool SelectedResponseWasNestedDecoded { get; set; }
}

public class HarValidationApiItem
{
    public string Key { get; set; } = string.Empty;

    public DateTime? StartedAt { get; set; }

    public string Method { get; set; } = "GET";

    public string Url { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string DisplayPath { get; set; } = string.Empty;

    public int? StatusCode { get; set; }

    public double DurationMs { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public string Category { get; set; } = "other";

    public string? CorrelationId { get; set; }

    public string? TraceId { get; set; }

    public string? SpanId { get; set; }

    public string? RequestId { get; set; }

    public bool IsLoadDashboard { get; set; }

    public bool IsSlow { get; set; }
}

public class HarKeyValueItem
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public class JsonTreeNode
{
    public string Key { get; set; } = string.Empty;

    public string NodeType { get; set; } = string.Empty;

    public string? ValuePreview { get; set; }

    public bool IsExpandedByDefault { get; set; }

    public List<JsonTreeNode> Children { get; set; } = [];
}
