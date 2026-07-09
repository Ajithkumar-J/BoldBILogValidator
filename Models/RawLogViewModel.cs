namespace BoldLogValidator.Models;

public class RawLogViewModel
{
    public RawLogViewFilter Filter { get; set; } = new();

    public string ActiveSourceLabel { get; set; } = string.Empty;

    public string ActiveSourcePath { get; set; } = string.Empty;

    public string? UploadSessionId { get; set; }

    public List<string> ServiceOptions { get; set; } = [];

    public List<RawLogFileOption> FileOptions { get; set; } = [];

    public string? FileContent { get; set; }

    public bool IsSearchResult { get; set; }

    public List<RawLogSearchHit> SearchHits { get; set; } = [];

    public List<string> Notes { get; set; } = [];
}

public class RawLogFileOption
{
    public string Value { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Service { get; set; } = string.Empty;
}

public class RawLogSearchHit
{
    public string Service { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public int LineNumber { get; set; }

    public string LineText { get; set; } = string.Empty;
}
