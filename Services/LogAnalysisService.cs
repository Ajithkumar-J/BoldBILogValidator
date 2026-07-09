using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BoldLogValidator.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace BoldLogValidator.Services;

public partial class LogAnalysisService : ILogAnalysisService
{
    private const int HighlightLimit = 150;
    private readonly string _activeUploadRoot;
    private readonly string _activeUploadLogRoot;
    private readonly string _activeUploadHarRoot;
    private readonly string _activeUploadMetadataPath;
    private readonly string _activeUploadHarMetadataPath;
    private readonly IMemoryCache _memoryCache;

    public LogAnalysisService(IWebHostEnvironment environment, IMemoryCache memoryCache)
    {
        _memoryCache = memoryCache;
        _activeUploadRoot = Path.Combine(environment.ContentRootPath, "App_Data", "CurrentUpload");
        _activeUploadLogRoot = Path.Combine(_activeUploadRoot, "logs");
        _activeUploadHarRoot = Path.Combine(_activeUploadRoot, "har");
        _activeUploadMetadataPath = Path.Combine(_activeUploadRoot, "upload-session.txt");
        _activeUploadHarMetadataPath = Path.Combine(_activeUploadRoot, "har-session.txt");
        Directory.CreateDirectory(_activeUploadRoot);
    }

    public async Task<AnalysisResult> AnalyzeAsync(AnalysisFilterInput filter, CancellationToken cancellationToken = default)
    {
        var shouldUseLocalLogPath = filter.LogFiles.Count == 0 && !string.IsNullOrWhiteSpace(filter.LocalLogPath);

        var result = new AnalysisResult
        {
            Filter = filter,
            UploadSessionId = filter.UploadSessionId,
            UploadedFileCount = filter.LogFiles.Count,
            BrowserTimeZone = string.IsNullOrWhiteSpace(filter.BrowserTimeZone) ? "UTC" : filter.BrowserTimeZone
        };

        result.Filter.UseLocalLogPath = shouldUseLocalLogPath;

        var effectiveFrom = filter.FromUtc ?? filter.From;
        var effectiveTo = filter.ToUtc ?? filter.To;

        var entries = new List<ParsedLogEntry>();
        if (shouldUseLocalLogPath)
        {
            if (!Directory.Exists(filter.LocalLogPath))
            {
                result.Notes.Add($"The local log path was not found: {filter.LocalLogPath}");
                return result;
            }

            var localFiles = CollectLocalLogFiles(filter.LocalLogPath, filter.SpecificService);
            result.UploadedFileCount = localFiles.Count;
            if (localFiles.Count == 0)
            {
                result.Notes.Add("No log files were found in the selected local path.");
                return result;
            }

            foreach (var filePath in localFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.AddRange(await ParseLogFileAsync(filePath, filter.LocalLogPath, filter.SpecificService, cancellationToken));
            }

            result.Notes.Add($"Local path analysis is enabled. Reading files directly from {filter.LocalLogPath} avoids browser upload errors for live rotating logs.");
        }
        else
        {
            var uploadSession = await PrepareUploadSessionAsync(filter, cancellationToken);
            result.UploadSessionId = uploadSession.SessionId;
            result.Filter.UploadSessionId = uploadSession.SessionId;
            result.UploadedFileCount = uploadSession.LogFiles.Count;

            if (uploadSession.LogFiles.Count == 0)
            {
                result.Notes.Add("Upload the full logs folder or one service folder to start validation, or enable local path analysis.");
                return result;
            }

            foreach (var filePath in uploadSession.LogFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.AddRange(await ParseLogFileAsync(filePath, uploadSession.LogRootPath, filter.SpecificService, cancellationToken));
            }

            result.Notes.Add(uploadSession.UsedSavedFiles
                ? $"Reused the previously uploaded files from session {uploadSession.SessionId}. Upload again only when you want to replace the source logs."
                : $"Saved the uploaded files to session {uploadSession.SessionId}. You can now change filters and analyze again without re-uploading.");
        }

        result.ParsedEntryCount = entries.Count;
        result.ErrorCount = entries.Count(static e => IsError(e.Severity));
        result.ServiceCount = entries.Select(static e => e.Service).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        result.RangeStart = entries.Count > 0 ? entries.Min(static e => e.Timestamp) : null;
        result.RangeEnd = entries.Count > 0 ? entries.Max(static e => e.Timestamp) : null;

        var harApis = await ParseHarRecordsAsync(filter, result.UploadSessionId, cancellationToken);

        result.HarApis = harApis;

        var derivedCorrelationIds = harApis
            .Select(static api => api.CorrelationId)
            .OfType<string>()
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var derivedTraceIds = harApis
            .Select(static api => api.TraceId)
            .OfType<string>()
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var derivedSpanIds = harApis
            .Select(static api => api.SpanId)
            .OfType<string>()
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(filter.CorrelationId))
        {
            result.AppliedIdentifiers.Add($"Correlation ID: {filter.CorrelationId}");
        }
        else if (derivedCorrelationIds.Count > 0)
        {
            result.AppliedIdentifiers.Add($"HAR Correlation IDs: {derivedCorrelationIds.Count}");
        }

        if (!string.IsNullOrWhiteSpace(filter.TraceId))
        {
            result.AppliedIdentifiers.Add($"Trace ID: {filter.TraceId}");
        }
        else if (derivedTraceIds.Count > 0)
        {
            result.AppliedIdentifiers.Add($"HAR Trace IDs: {derivedTraceIds.Count}");
        }

        if (!string.IsNullOrWhiteSpace(filter.SpanId))
        {
            result.AppliedIdentifiers.Add($"Span ID: {filter.SpanId}");
        }
        else if (derivedSpanIds.Count > 0)
        {
            result.AppliedIdentifiers.Add($"HAR Span IDs: {derivedSpanIds.Count}");
        }

        if (!string.IsNullOrWhiteSpace(filter.Keyword))
        {
            result.AppliedIdentifiers.Add($"Keyword: {filter.Keyword}");
        }

        var filteredEntries = entries
            .Where(entry => MatchesService(entry, filter.SpecificService))
            .Where(entry => MatchesSeverity(entry, filter.IncludeErrors, filter.IncludeDebugInfo))
            .Where(entry => MatchesDate(entry, effectiveFrom, effectiveTo))
            .Where(entry => MatchesIdentifier(entry.CorrelationId, filter.CorrelationId, derivedCorrelationIds))
            .Where(entry => MatchesIdentifier(entry.TraceId, filter.TraceId, derivedTraceIds))
            .Where(entry => MatchesIdentifier(entry.SpanId, filter.SpanId, derivedSpanIds))
            .Where(entry => MatchesKeyword(entry, filter.Keyword))
            .OrderByDescending(static entry => entry.Timestamp)
            .ToList();

        result.FilteredEntryCount = filteredEntries.Count;
        result.FilteredErrorCount = filteredEntries.Count(static e => IsError(e.Severity));
        result.HighlightedEntries = filteredEntries.Take(HighlightLimit).ToList();

        result.ServiceSummaries = filteredEntries
            .GroupBy(static entry => entry.Service, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var errors = group.Where(entry => IsError(entry.Severity)).ToList();
                var topError = errors
                    .GroupBy(static entry => entry.Signature)
                    .OrderByDescending(static g => g.Count())
                    .FirstOrDefault();

                return new ServiceSummary
                {
                    Service = group.Key,
                    TotalEntries = group.Count(),
                    ErrorCount = errors.Count,
                    WarningCount = group.Count(entry => entry.Severity.Equals("Warning", StringComparison.OrdinalIgnoreCase)),
                    DistinctCorrelationCount = group
                        .Select(static entry => entry.CorrelationId)
                        .Where(static value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    TopErrorSummary = topError == null
                        ? "No errors found"
                        : $"{topError.Count()} occurrence(s)",
                    TopErrorMessage = topError?.First().Message ?? string.Empty,
                    TopErrorCount = topError?.Count() ?? 0
                };
            })
            .OrderByDescending(static summary => summary.ErrorCount)
            .ThenBy(static summary => summary.Service)
            .ToList();

        result.SignatureSummaries = filteredEntries
            .Where(entry => IsError(entry.Severity))
            .GroupBy(static entry => entry.Signature)
            .Select(group => new SignatureSummary
            {
                Signature = group.Key,
                Count = group.Count(),
                Services = string.Join(", ", group.Select(static entry => entry.Service).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static value => value)),
                ExampleMessage = group.First().Message
            })
            .OrderByDescending(static summary => summary.Count)
            .ThenBy(static summary => summary.Signature)
            .Take(20)
            .ToList();

        result.GroupedLogSummaries = filteredEntries
            .GroupBy(entry => new
            {
                ServiceKey = entry.Service.ToUpperInvariant(),
                SignatureKey = entry.Signature.ToUpperInvariant(),
                entry.Service,
                entry.Signature
            })
            .Select(group => new GroupedLogSummary
            {
                Service = group.Key.Service,
                Signature = group.Key.Signature,
                Count = group.Count(),
                ExampleMessage = group.First().Message,
                Severity = group.First().Severity
            })
            .OrderByDescending(static summary => summary.Count)
            .ThenBy(static summary => summary.Service)
            .ThenBy(static summary => summary.Signature)
            .ToList();

        if (filter.EnableConcurrentInsights)
        {
            result.ConcurrentInsights = BuildConcurrentInsights(filteredEntries);
        }

        if (harApis.Count > 0)
        {
            result.Notes.Add($"HAR upload processed successfully. Matched {harApis.Count} API entries for correlation.");
        }

        result.Notes.Add("Log timestamps are treated as UTC internally. The UI can show and filter them using the browser's local timezone.");

        if (result.FilteredEntryCount == 0)
        {
            result.Notes.Add("No log lines matched the selected filters. Try widening the date range or clearing identifier filters.");
        }

        return result;
    }

    public async Task<HarValidationResult> GetHarValidationAsync(HarValidationFilterInput filter, CancellationToken cancellationToken = default)
    {
        var result = new HarValidationResult
        {
            Filter = filter,
            BrowserTimeZone = string.IsNullOrWhiteSpace(filter.BrowserTimeZone) ? "UTC" : filter.BrowserTimeZone
        };

        var effectiveFrom = filter.FromUtc ?? filter.From;
        var effectiveTo = filter.ToUtc ?? filter.To;
        var source = await PrepareHarValidationSourceAsync(filter.HarFile, cancellationToken);

        result.ActiveHarFileName = source.FileName;
        result.ActiveHarSourceLabel = source.UsedSavedFile
            ? $"Using cached HAR: {source.FileName}"
            : $"Loaded HAR: {source.FileName}";

        if (string.IsNullOrWhiteSpace(source.FilePath) || !File.Exists(source.FilePath))
        {
            result.Notes.Add("Upload a HAR file once to start dashboard API validation.");
            return result;
        }

        var parsedBundle = await GetCachedHarValidationBundleAsync(source.FilePath, cancellationToken);
        result.DashboardPath = parsedBundle.DashboardPath;
        result.EnvironmentLabel = parsedBundle.EnvironmentLabel;
        result.CategoryOptions = parsedBundle.Entries
            .Select(static entry => entry.Category)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(filter.Keyword))
        {
            result.AppliedFilters.Add($"Keyword: {filter.Keyword}");
        }

        if (!string.IsNullOrWhiteSpace(filter.CorrelationId))
        {
            result.AppliedFilters.Add($"Correlation ID: {filter.CorrelationId}");
        }

        if (!string.IsNullOrWhiteSpace(filter.TraceId))
        {
            result.AppliedFilters.Add($"Trace ID: {filter.TraceId}");
        }

        if (!string.Equals(filter.Method, "all", StringComparison.OrdinalIgnoreCase))
        {
            result.AppliedFilters.Add($"Method: {filter.Method.ToUpperInvariant()}");
        }

        if (!string.Equals(filter.StatusFamily, "all", StringComparison.OrdinalIgnoreCase))
        {
            result.AppliedFilters.Add($"Status: {filter.StatusFamily}");
        }

        if (!string.Equals(filter.ApiCategory, "all", StringComparison.OrdinalIgnoreCase))
        {
            result.AppliedFilters.Add($"Category: {filter.ApiCategory}");
        }

        var filteredEntries = parsedBundle.Entries
            .Where(static entry => entry.IsApiCandidate)
            .Where(entry => MatchesHarKeyword(entry, filter.Keyword))
            .Where(entry => MatchesHarMethod(entry, filter.Method))
            .Where(entry => MatchesHarStatusFamily(entry, filter.StatusFamily))
            .Where(entry => MatchesHarCategory(entry, filter.ApiCategory))
            .Where(entry => MatchesHarIdentifier(entry.CorrelationId, filter.CorrelationId))
            .Where(entry => MatchesHarIdentifier(entry.TraceId, filter.TraceId))
            .Where(entry => MatchesHarDate(entry.StartedAt, effectiveFrom, effectiveTo))
            .ToList();

        result.FilteredApis = filteredEntries
            .Select(entry => new HarValidationApiItem
            {
                Key = entry.Key,
                StartedAt = entry.StartedAt,
                Method = entry.Method,
                Url = entry.Url,
                Path = entry.Path,
                DisplayPath = entry.DisplayPath,
                StatusCode = entry.StatusCode,
                DurationMs = entry.DurationMs,
                ContentType = entry.ContentType,
                Category = entry.Category,
                CorrelationId = entry.CorrelationId,
                TraceId = entry.TraceId,
                SpanId = entry.SpanId,
                RequestId = entry.RequestId,
                IsLoadDashboard = entry.IsLoadDashboard,
                IsSlow = entry.DurationMs >= 1000
            })
            .ToList();

        result.TotalApis = result.FilteredApis.Count;
        result.DistinctEndpoints = result.FilteredApis
            .Select(static entry => entry.DisplayPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        result.ErrorApis = result.FilteredApis.Count(entry => entry.StatusCode >= 400);
        result.SlowApis = result.FilteredApis.Count(static entry => entry.IsSlow);
        result.LoadDashboardHits = result.FilteredApis.Count(static entry => entry.IsLoadDashboard);
        result.AverageResponseTimeMs = result.FilteredApis.Count == 0
            ? 0
            : Math.Round(result.FilteredApis.Average(static entry => entry.DurationMs), 1);

        var selectedEntry = filteredEntries.FirstOrDefault(entry => string.Equals(entry.Key, filter.SelectedRequestKey, StringComparison.Ordinal))
            ?? filteredEntries.FirstOrDefault(static entry => entry.IsLoadDashboard)
            ?? filteredEntries.FirstOrDefault();

        if (selectedEntry != null)
        {
            ApplyHarRequestDetails(result, selectedEntry);
        }

        result.StatusChips.Add("HAR parsed");
        if (result.SelectedRequestHeaders.Count > 0 || result.SelectedResponseHeaders.Count > 0)
        {
            result.StatusChips.Add("Headers extracted");
        }

        if (result.SelectedResponseTree != null)
        {
            result.StatusChips.Add("JSON response formatted");
        }

        if (!string.IsNullOrWhiteSpace(result.DashboardPath))
        {
            result.Notes.Add($"Dashboard path detected from HAR: {result.DashboardPath}");
        }

        if (result.FilteredApis.Count == 0)
        {
            result.Notes.Add("No API entries matched the selected HAR filters. Try widening the status, method, or keyword filters.");
        }

        return result;
    }

    public async Task<HarRequestDetailsResult> GetHarRequestDetailsAsync(string? requestKey, CancellationToken cancellationToken = default)
    {
        var source = await PrepareHarValidationSourceAsync(null, cancellationToken);
        if (string.IsNullOrWhiteSpace(source.FilePath) || !File.Exists(source.FilePath))
        {
            return new HarRequestDetailsResult();
        }

        var parsedBundle = await GetCachedHarValidationBundleAsync(source.FilePath, cancellationToken);
        var selectedEntry = parsedBundle.Entries
            .Where(static entry => entry.IsApiCandidate)
            .FirstOrDefault(entry => string.Equals(entry.Key, requestKey, StringComparison.Ordinal))
            ?? parsedBundle.Entries.FirstOrDefault(static entry => entry.IsApiCandidate && entry.IsLoadDashboard)
            ?? parsedBundle.Entries.FirstOrDefault(static entry => entry.IsApiCandidate);

        if (selectedEntry == null)
        {
            return new HarRequestDetailsResult();
        }

        return BuildHarRequestDetailsResult(selectedEntry);
    }

    public async Task<RawLogViewModel> GetRawLogViewAsync(RawLogViewFilter filter, CancellationToken cancellationToken = default)
    {
        var model = new RawLogViewModel
        {
            Filter = filter,
            UploadSessionId = filter.UploadSessionId
        };

        if (!TryResolveRawLogRoot(filter.UseLocalLogPath, filter.LocalLogPath, out var rootPath, out var sourceLabel, out var note))
        {
            if (!string.IsNullOrWhiteSpace(note))
            {
                model.Notes.Add(note);
            }

            return model;
        }

        model.ActiveSourceLabel = sourceLabel;
        model.ActiveSourcePath = rootPath;
        model.UploadSessionId = filter.UseLocalLogPath ? null : ReadCurrentUploadSessionId();
        model.Filter.UploadSessionId = model.UploadSessionId;

        var rawLogFiles = CollectRawLogFiles(rootPath);
        if (rawLogFiles.Count == 0)
        {
            model.Notes.Add("No text log files were found in the selected source.");
            return model;
        }

        var fileOptions = rawLogFiles
            .Select(path =>
            {
                var relativePath = Path.GetRelativePath(rootPath, path).Replace('\\', '/');
                return new RawLogFileOption
                {
                    Value = relativePath,
                    Label = relativePath,
                    Service = ResolveServiceName(relativePath, null)
                };
            })
            .OrderBy(static option => option.Service)
            .ThenBy(static option => option.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        model.ServiceOptions = fileOptions
            .Select(static option => option.Service)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static service => service, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(filter.SelectedService))
        {
            fileOptions = fileOptions
                .Where(option => option.Service.Equals(filter.SelectedService, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        model.FileOptions = fileOptions;

        if (!string.IsNullOrWhiteSpace(filter.SelectedFile))
        {
            var selectedOption = fileOptions.FirstOrDefault(option => option.Value.Equals(filter.SelectedFile, StringComparison.OrdinalIgnoreCase))
                ?? model.FileOptions.FirstOrDefault(option => option.Value.Equals(filter.SelectedFile, StringComparison.OrdinalIgnoreCase));
            if (selectedOption != null)
            {
                var selectedPath = Path.Combine(rootPath, selectedOption.Value.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(selectedPath))
                {
                    model.FileContent = await File.ReadAllTextAsync(selectedPath, cancellationToken);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            model.IsSearchResult = true;
            var searchScope = filter.SearchAllFiles || string.IsNullOrWhiteSpace(filter.SelectedFile)
                ? model.FileOptions
                : model.FileOptions.Where(option => option.Value.Equals(filter.SelectedFile, StringComparison.OrdinalIgnoreCase)).ToList();
            model.SearchHits = await SearchRawLogsAsync(rootPath, searchScope, filter.SearchTerm, cancellationToken);

            if (model.SearchHits.Count == 0)
            {
                model.Notes.Add("No matching lines were found for the current search term.");
            }
        }
        else if (string.IsNullOrWhiteSpace(filter.SelectedFile))
        {
            model.Notes.Add("Choose a service and file to read the raw content, or enter a search term to scan the logs.");
        }

        return model;
    }

    private async Task<HarValidationSourceState> PrepareHarValidationSourceAsync(IFormFile? harFile, CancellationToken cancellationToken)
    {
        var newUploadReceived = harFile is { Length: > 0 };
        string? fileName = null;

        if (newUploadReceived)
        {
            fileName = GetSafeFileName(harFile!.FileName, "upload.har");
            ClearDirectory(_activeUploadHarRoot);
            Directory.CreateDirectory(_activeUploadHarRoot);
            var savedHarPath = Path.Combine(_activeUploadHarRoot, fileName);
            await SaveFormFileAsync(harFile, savedHarPath, cancellationToken);
            File.WriteAllText(_activeUploadHarMetadataPath, fileName);

            return new HarValidationSourceState
            {
                FilePath = savedHarPath,
                FileName = fileName,
                UsedSavedFile = false
            };
        }

        if (!Directory.Exists(_activeUploadHarRoot))
        {
            return new HarValidationSourceState();
        }

        var savedHarFile = Directory
            .EnumerateFiles(_activeUploadHarRoot)
            .OrderByDescending(static path => File.GetLastWriteTimeUtc(path))
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(savedHarFile))
        {
            return new HarValidationSourceState();
        }

        return new HarValidationSourceState
        {
            FilePath = savedHarFile,
            FileName = Path.GetFileName(savedHarFile),
            UsedSavedFile = true
        };
    }

    private static async Task<HarValidationParseBundle> ParseHarValidationBundleAsync(string filePath, CancellationToken cancellationToken)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var bundle = new HarValidationParseBundle();

        if (document.RootElement.TryGetProperty("log", out var logElement))
        {
            if (logElement.TryGetProperty("pages", out var pagesElement) &&
                pagesElement.ValueKind == JsonValueKind.Array &&
                pagesElement.GetArrayLength() > 0)
            {
                var firstPage = pagesElement[0];
                if (firstPage.TryGetProperty("title", out var titleElement))
                {
                    var title = titleElement.GetString();
                    bundle.DashboardPath = ExtractDashboardPath(title);
                    bundle.EnvironmentLabel = ExtractEnvironmentLabel(title);
                }
            }

            if (logElement.TryGetProperty("entries", out var entriesElement) && entriesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entryElement in entriesElement.EnumerateArray())
                {
                    var entry = ParseHarValidationEntry(entryElement);
                    if (entry != null)
                    {
                        bundle.Entries.Add(entry);
                        if (string.IsNullOrWhiteSpace(bundle.DashboardPath))
                        {
                            bundle.DashboardPath = entry.DashboardPath;
                        }

                        if (string.IsNullOrWhiteSpace(bundle.EnvironmentLabel))
                        {
                            bundle.EnvironmentLabel = entry.EnvironmentLabel;
                        }
                    }
                }
            }
        }

        return bundle;
    }

    private static HarValidationEntry? ParseHarValidationEntry(JsonElement entryElement)
    {
        if (!entryElement.TryGetProperty("request", out var requestElement))
        {
            return null;
        }

        var method = requestElement.TryGetProperty("method", out var methodElement) ? methodElement.GetString() ?? "GET" : "GET";
        var url = requestElement.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var path = string.Empty;
        var displayPath = url;
        var environmentLabel = string.Empty;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            path = uri.AbsolutePath;
            displayPath = string.IsNullOrWhiteSpace(path) ? uri.AbsoluteUri : path;
            environmentLabel = $"{uri.Host}{(uri.AbsolutePath.StartsWith("/bi/", StringComparison.OrdinalIgnoreCase) ? " /bi" : string.Empty)}";
        }

        var requestHeaders = ReadHarNameValueArray(requestElement, "headers");
        var queryParameters = ReadHarNameValueArray(requestElement, "queryString");
        var requestPayloadText = requestElement.TryGetProperty("postData", out var postDataElement) &&
            postDataElement.TryGetProperty("text", out var requestTextElement)
            ? requestTextElement.GetString()
            : null;

        int? statusCode = null;
        var responseHeaders = new List<HarKeyValueItem>();
        var responseText = default(string);
        var contentType = string.Empty;
        if (entryElement.TryGetProperty("response", out var responseElement))
        {
            if (responseElement.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.Number)
            {
                statusCode = statusElement.GetInt32();
            }

            responseHeaders = ReadHarNameValueArray(responseElement, "headers");

            if (responseElement.TryGetProperty("content", out var contentElement))
            {
                if (contentElement.TryGetProperty("mimeType", out var mimeTypeElement))
                {
                    contentType = mimeTypeElement.GetString() ?? string.Empty;
                }

                if (contentElement.TryGetProperty("text", out var responseTextElement))
                {
                    responseText = responseTextElement.GetString();
                }
            }
        }

        var startedAt = entryElement.TryGetProperty("startedDateTime", out var startedElement) &&
                        startedElement.ValueKind == JsonValueKind.String
            ? startedElement.GetDateTime()
            : (DateTime?)null;

        double durationMs = 0;
        if (entryElement.TryGetProperty("time", out var timeElement) && timeElement.ValueKind == JsonValueKind.Number)
        {
            durationMs = timeElement.GetDouble();
        }

        var headerMap = requestHeaders
            .Concat(responseHeaders)
            .GroupBy(static header => header.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Value, StringComparer.OrdinalIgnoreCase);

        if (!headerMap.TryGetValue("Content-Type", out var detectedContentType) && !string.IsNullOrWhiteSpace(contentType))
        {
            detectedContentType = contentType;
        }

        headerMap.TryGetValue("correlationId", out var correlationId);
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            headerMap.TryGetValue("x-correlation-id", out correlationId);
        }

        headerMap.TryGetValue("traceId", out var traceId);
        headerMap.TryGetValue("spanId", out var spanId);
        headerMap.TryGetValue("requestId", out var requestId);
        if (string.IsNullOrWhiteSpace(requestId))
        {
            headerMap.TryGetValue("request-id", out requestId);
        }

        if (headerMap.TryGetValue("traceparent", out var traceParent) && !string.IsNullOrWhiteSpace(traceParent))
        {
            var parts = traceParent.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 4)
            {
                traceId ??= parts[1];
                spanId ??= parts[2];
            }
        }

        var category = ClassifyHarCategory(path);
        var dashboardPath = requestHeaders
            .FirstOrDefault(static header => header.Name.Equals("DashboardPath", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        var isLoadDashboard = path.Contains("loaddashboard", StringComparison.OrdinalIgnoreCase);
        return new HarValidationEntry
        {
            Key = BuildHarEntryKey(method, url, startedAt, durationMs),
            StartedAt = startedAt,
            Method = method,
            Url = url,
            Path = path,
            DisplayPath = displayPath,
            StatusCode = statusCode,
            DurationMs = durationMs,
            ContentType = detectedContentType ?? string.Empty,
            Category = category,
            CorrelationId = correlationId,
            TraceId = traceId,
            SpanId = spanId,
            RequestId = requestId,
            RequestHeaders = requestHeaders,
            ResponseHeaders = responseHeaders,
            QueryParameters = queryParameters,
            RequestPayloadText = requestPayloadText,
            ResponseText = responseText,
            DashboardPath = dashboardPath,
            EnvironmentLabel = environmentLabel,
            IsLoadDashboard = isLoadDashboard,
            IsApiCandidate = IsApiCandidate(path, method, detectedContentType, responseText)
        };
    }

    private static List<HarKeyValueItem> ReadHarNameValueArray(JsonElement parentElement, string propertyName)
    {
        if (!parentElement.TryGetProperty(propertyName, out var arrayElement) || arrayElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var items = new List<HarKeyValueItem>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var value = item.TryGetProperty("value", out var valueElement) ? valueElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            items.Add(new HarKeyValueItem
            {
                Name = name,
                Value = value ?? string.Empty
            });
        }

        return items;
    }

    private static string BuildHarEntryKey(string method, string url, DateTime? startedAt, double durationMs)
    {
        return $"{method}|{url}|{startedAt:O}|{durationMs.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    private static string ClassifyHarCategory(string path)
    {
        if (path.Contains("loaddashboard", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("dashboard", StringComparison.OrdinalIgnoreCase))
        {
            return "dashboard";
        }

        if (path.Contains("widget", StringComparison.OrdinalIgnoreCase))
        {
            return "widget";
        }

        if (path.Contains("auth", StringComparison.OrdinalIgnoreCase))
        {
            return "auth";
        }

        if (path.Contains("metadata", StringComparison.OrdinalIgnoreCase))
        {
            return "metadata";
        }

        if (path.Contains("filter", StringComparison.OrdinalIgnoreCase))
        {
            return "filter";
        }

        if (path.Contains("/designer/", StringComparison.OrdinalIgnoreCase))
        {
            return "designer";
        }

        return "other";
    }

    private static bool IsApiCandidate(string path, string method, string? contentType, string? responseText)
    {
        if (path.Contains("/api/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/designer/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(contentType) &&
            (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
             contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase) == false && contentType.Contains("text", StringComparison.OrdinalIgnoreCase) == false))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(responseText) &&
               responseText.TrimStart().StartsWith("{", StringComparison.Ordinal);
    }

    private static string? ExtractDashboardPath(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || !Uri.TryCreate(title, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var marker = "/dashboards/";
        var index = uri.AbsolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return uri.AbsolutePath.Trim('/');
        }

        return uri.AbsolutePath[(index + marker.Length)..].Trim('/');
    }

    private static string? ExtractEnvironmentLabel(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || !Uri.TryCreate(title, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return $"{uri.Host}{(uri.AbsolutePath.StartsWith("/bi/", StringComparison.OrdinalIgnoreCase) ? " /bi" : string.Empty)}";
    }

    private static string? NormalizeDisplayText(string? text)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private async Task<HarValidationParseBundle> GetCachedHarValidationBundleAsync(string filePath, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(filePath);
        var cacheKey = $"har-bundle::{fileInfo.FullName}::{fileInfo.LastWriteTimeUtc.Ticks}::{fileInfo.Length}";

        return await _memoryCache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            entry.SlidingExpiration = TimeSpan.FromMinutes(10);
            return await ParseHarValidationBundleAsync(filePath, cancellationToken);
        }) ?? new HarValidationParseBundle();
    }

    private static void ApplyHarRequestDetails(HarValidationResult result, HarValidationEntry selectedEntry)
    {
        var details = BuildHarRequestDetailsResult(selectedEntry);
        result.Filter.SelectedRequestKey = details.SelectedRequestKey;
        result.SelectedApi = details.SelectedApi;
        result.SelectedRequestHeaders = details.SelectedRequestHeaders;
        result.SelectedResponseHeaders = details.SelectedResponseHeaders;
        result.SelectedQueryParameters = details.SelectedQueryParameters;
        result.SelectedPayloadText = details.SelectedPayloadText;
        result.SelectedPayloadTree = details.SelectedPayloadTree;
        result.SelectedPayloadWasNestedDecoded = details.SelectedPayloadWasNestedDecoded;
        result.SelectedResponseText = details.SelectedResponseText;
        result.SelectedResponseTree = details.SelectedResponseTree;
        result.SelectedResponseWasNestedDecoded = details.SelectedResponseWasNestedDecoded;
    }

    private static HarRequestDetailsResult BuildHarRequestDetailsResult(HarValidationEntry selectedEntry)
    {
        var payloadTree = BuildJsonTree(selectedEntry.RequestPayloadText, "Payload", out var payloadNestedDecoded);
        var responseTree = BuildJsonTree(selectedEntry.ResponseText, "Response", out var responseNestedDecoded);

        return new HarRequestDetailsResult
        {
            SelectedRequestKey = selectedEntry.Key,
            SelectedApi = new HarValidationApiItem
            {
                Key = selectedEntry.Key,
                StartedAt = selectedEntry.StartedAt,
                Method = selectedEntry.Method,
                Url = selectedEntry.Url,
                Path = selectedEntry.Path,
                DisplayPath = selectedEntry.DisplayPath,
                StatusCode = selectedEntry.StatusCode,
                DurationMs = selectedEntry.DurationMs,
                ContentType = selectedEntry.ContentType,
                Category = selectedEntry.Category,
                CorrelationId = selectedEntry.CorrelationId,
                TraceId = selectedEntry.TraceId,
                SpanId = selectedEntry.SpanId,
                RequestId = selectedEntry.RequestId,
                IsLoadDashboard = selectedEntry.IsLoadDashboard,
                IsSlow = selectedEntry.DurationMs >= 1000
            },
            SelectedRequestHeaders = selectedEntry.RequestHeaders,
            SelectedResponseHeaders = selectedEntry.ResponseHeaders,
            SelectedQueryParameters = selectedEntry.QueryParameters,
            SelectedPayloadText = NormalizeDisplayText(selectedEntry.RequestPayloadText),
            SelectedPayloadTree = payloadTree,
            SelectedPayloadWasNestedDecoded = payloadNestedDecoded,
            SelectedResponseText = NormalizeDisplayText(selectedEntry.ResponseText),
            SelectedResponseTree = responseTree,
            SelectedResponseWasNestedDecoded = responseNestedDecoded
        };
    }

    private static JsonTreeNode? BuildJsonTree(string? responseText, string rootKey, out bool nestedDecoded)
    {
        nestedDecoded = false;
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        var node = ParseJsonNode(responseText.Trim());
        if (node == null)
        {
            return null;
        }

        return BuildJsonTreeNode(rootKey, node, 0, ref nestedDecoded);
    }

    private static JsonTreeNode BuildJsonTreeNode(string key, JsonNode? node, int depth, ref bool nestedDecoded)
    {
        if (node is JsonObject jsonObject)
        {
            var treeNode = new JsonTreeNode
            {
                Key = key,
                NodeType = "object",
                ValuePreview = $"{jsonObject.Count} field(s)",
                IsExpandedByDefault = depth < 2
            };

            foreach (var property in jsonObject)
            {
                treeNode.Children.Add(BuildJsonTreeNode(property.Key, property.Value, depth + 1, ref nestedDecoded));
            }

            return treeNode;
        }

        if (node is JsonArray jsonArray)
        {
            var treeNode = new JsonTreeNode
            {
                Key = key,
                NodeType = "array",
                ValuePreview = $"{jsonArray.Count} item(s)",
                IsExpandedByDefault = depth < 2
            };

            for (var index = 0; index < jsonArray.Count; index++)
            {
                treeNode.Children.Add(BuildJsonTreeNode($"[{index}]", jsonArray[index], depth + 1, ref nestedDecoded));
            }

            return treeNode;
        }

        if (node is JsonValue valueNode)
        {
            var valueText = valueNode.ToJsonString();
            if (valueNode.TryGetValue<string>(out var stringValue))
            {
                var nestedNode = ParseJsonNode(stringValue);
                if (nestedNode != null)
                {
                    nestedDecoded = true;
                    return BuildJsonTreeNode(key, nestedNode, depth, ref nestedDecoded);
                }

                valueText = stringValue;
            }

            return new JsonTreeNode
            {
                Key = key,
                NodeType = "value",
                ValuePreview = ShortenValue(valueText),
                IsExpandedByDefault = false
            };
        }

        return new JsonTreeNode
        {
            Key = key,
            NodeType = "value",
            ValuePreview = "null",
            IsExpandedByDefault = false
        };
    }

    private static JsonNode? ParseJsonNode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (!(trimmed.StartsWith('{') || trimmed.StartsWith('[')))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(trimmed);
        }
        catch
        {
            return null;
        }
    }

    private static string ShortenValue(string value)
    {
        var normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= 180 ? normalized : $"{normalized[..177]}...";
    }

    private static bool MatchesHarKeyword(HarValidationEntry entry, string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return true;
        }

        var term = keyword.Trim();
        return entry.Url.Contains(term, StringComparison.OrdinalIgnoreCase)
            || entry.DisplayPath.Contains(term, StringComparison.OrdinalIgnoreCase)
            || entry.RequestPayloadText?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
            || entry.ResponseText?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool MatchesHarMethod(HarValidationEntry entry, string? method)
    {
        return string.IsNullOrWhiteSpace(method) ||
               string.Equals(method, "all", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(entry.Method, method, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesHarStatusFamily(HarValidationEntry entry, string? statusFamily)
    {
        if (string.IsNullOrWhiteSpace(statusFamily) || string.Equals(statusFamily, "all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!entry.StatusCode.HasValue)
        {
            return false;
        }

        return statusFamily.ToLowerInvariant() switch
        {
            "2xx" => entry.StatusCode.Value is >= 200 and < 300,
            "3xx" => entry.StatusCode.Value is >= 300 and < 400,
            "4xx" => entry.StatusCode.Value is >= 400 and < 500,
            "5xx" => entry.StatusCode.Value is >= 500 and < 600,
            _ => true
        };
    }

    private static bool MatchesHarCategory(HarValidationEntry entry, string? category)
    {
        return string.IsNullOrWhiteSpace(category) ||
               string.Equals(category, "all", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(entry.Category, category, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesHarIdentifier(string? value, string? filterValue)
    {
        return string.IsNullOrWhiteSpace(filterValue) ||
               string.Equals(value, filterValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesHarDate(DateTime? startedAt, DateTime? from, DateTime? to)
    {
        if (!startedAt.HasValue)
        {
            return !from.HasValue && !to.HasValue;
        }

        if (from.HasValue && startedAt.Value < from.Value)
        {
            return false;
        }

        if (to.HasValue && startedAt.Value > to.Value)
        {
            return false;
        }

        return true;
    }

    private async Task<List<HarApiRecord>> ParseHarRecordsAsync(AnalysisFilterInput filter, string? uploadSessionId, CancellationToken cancellationToken)
    {
        if (filter.HarFile is { Length: > 0 })
        {
            return await ParseHarAsync(filter.HarFile, cancellationToken);
        }

        return [];
    }

    private async Task<List<ParsedLogEntry>> ParseLogFileAsync(string filePath, string rootPath, string? serviceHint, CancellationToken cancellationToken)
    {
        var entries = new List<ParsedLogEntry>();
        var normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedFile = Path.GetFullPath(filePath);
        var relativePath = Path.GetRelativePath(normalizedRoot, normalizedFile).Replace('\\', '/');
        var service = ResolveServiceName(relativePath, serviceHint);
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var lineNumber = 0;
        var entryStartLineNumber = 0;
        var entryBuilder = new StringBuilder();
        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            if (IsLogEntryStart(line))
            {
                if (entryBuilder.Length > 0)
                {
                    var bufferedEntry = TryParseLine(entryBuilder.ToString(), service, relativePath, Path.GetFileName(relativePath), entryStartLineNumber);
                    if (bufferedEntry != null)
                    {
                        entries.Add(bufferedEntry);
                    }

                    entryBuilder.Clear();
                }

                entryStartLineNumber = lineNumber;
                entryBuilder.Append(line);
            }
            else if (entryBuilder.Length > 0)
            {
                entryBuilder.AppendLine();
                entryBuilder.Append(line);
            }
        }

        if (entryBuilder.Length > 0)
        {
            var bufferedEntry = TryParseLine(entryBuilder.ToString(), service, relativePath, Path.GetFileName(relativePath), entryStartLineNumber);
            if (bufferedEntry != null)
            {
                entries.Add(bufferedEntry);
            }
        }

        return entries;
    }

    private static List<string> CollectLocalLogFiles(string localLogPath, string? specificService)
    {
        var candidateRoot = localLogPath.Trim();
        if (!string.IsNullOrWhiteSpace(specificService))
        {
            var servicePath = Path.Combine(candidateRoot, specificService);
            if (Directory.Exists(servicePath))
            {
                candidateRoot = servicePath;
            }
        }

        return Directory
            .EnumerateFiles(candidateRoot, "*.txt*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var fileName = Path.GetFileName(path);
                return fileName.StartsWith("errors", StringComparison.OrdinalIgnoreCase)
                    || fileName.StartsWith("debug", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private ParsedLogEntry? TryParseLine(string line, string service, string relativePath, string fileName, int lineNumber)
    {
        var firstLine = line.Split(Environment.NewLine, 2, StringSplitOptions.None)[0];
        var continuationText = line.Length > firstLine.Length
            ? line[(firstLine.Length + Environment.NewLine.Length)..]
            : string.Empty;

        var match = LogLineRegex().Match(firstLine);
        if (!match.Success)
        {
            return null;
        }

        if (!DateTime.TryParseExact(match.Groups["timestamp"].Value, "yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
        {
            return null;
        }

        timestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);

        var severity = match.Groups["level"].Success ? match.Groups["level"].Value : InferSeverity(fileName);
        var remainder = match.Groups["rest"].Value.Trim();
        if (!string.IsNullOrWhiteSpace(continuationText))
        {
            remainder = string.Concat(remainder, Environment.NewLine, continuationText);
        }

        var cleanedMessage = IdRegex().Replace(remainder, string.Empty).Trim();
        cleanedMessage = Regex.Replace(cleanedMessage, @"[ \t]{2,}", " ");

        return new ParsedLogEntry
        {
            Timestamp = timestamp,
            Service = service,
            RelativePath = relativePath,
            FileName = fileName,
            Severity = string.IsNullOrWhiteSpace(severity) ? "Info" : severity,
            CorrelationId = ExtractValue(remainder, "correlationId"),
            TraceId = ExtractValue(remainder, "traceId"),
            SpanId = ExtractValue(remainder, "spanId"),
            RequestId = ExtractValue(remainder, "requestId"),
            Operation = ExtractValue(remainder, "operation"),
            Stage = ExtractValue(remainder, "stage"),
            Message = cleanedMessage,
            Signature = NormalizeSignature(cleanedMessage),
            RawLine = line,
            LineNumber = lineNumber
        };
    }

    private async Task<UploadSessionState> PrepareUploadSessionAsync(AnalysisFilterInput filter, CancellationToken cancellationToken)
    {
        var newUploadReceived = filter.LogFiles.Count > 0;
        var sessionId = ReadCurrentUploadSessionId();

        if (newUploadReceived)
        {
            sessionId = Guid.NewGuid().ToString("N");
            ClearDirectory(_activeUploadLogRoot);
            Directory.CreateDirectory(_activeUploadLogRoot);
            foreach (var file in filter.LogFiles.Where(static file => file.Length > 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetPath = BuildUploadedFilePath(_activeUploadLogRoot, file.FileName);
                await SaveFormFileAsync(file, targetPath, cancellationToken);
            }

            File.WriteAllText(_activeUploadMetadataPath, sessionId);
        }

        var savedLogFiles = Directory.Exists(_activeUploadLogRoot)
            ? Directory.EnumerateFiles(_activeUploadLogRoot, "*.txt*", SearchOption.AllDirectories)
                .Where(path =>
                {
                    var fileName = Path.GetFileName(path);
                    return fileName.StartsWith("errors", StringComparison.OrdinalIgnoreCase)
                        || fileName.StartsWith("debug", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        return new UploadSessionState
        {
            SessionId = sessionId,
            LogRootPath = _activeUploadLogRoot,
            LogFiles = savedLogFiles,
            UsedSavedFiles = !newUploadReceived && savedLogFiles.Count > 0
        };
    }

    private string ReadCurrentUploadSessionId()
    {
        if (!File.Exists(_activeUploadMetadataPath))
        {
            return string.IsNullOrWhiteSpace(_activeUploadRoot) ? Guid.NewGuid().ToString("N") : "not-uploaded-yet";
        }

        var savedId = File.ReadAllText(_activeUploadMetadataPath).Trim();
        return string.IsNullOrWhiteSpace(savedId) ? "not-uploaded-yet" : savedId;
    }

    private static void ClearDirectory(string directoryPath)
    {
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private bool TryResolveRawLogRoot(bool useLocalLogPath, string? localLogPath, out string rootPath, out string sourceLabel, out string? note)
    {
        note = null;
        if (useLocalLogPath)
        {
            rootPath = string.IsNullOrWhiteSpace(localLogPath) ? string.Empty : localLogPath.Trim();
            sourceLabel = "Local installed logs";
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                note = string.IsNullOrWhiteSpace(rootPath)
                    ? "Enter a valid local log path to inspect raw files."
                    : $"The local log path was not found: {rootPath}";
                return false;
            }

            return true;
        }

        rootPath = _activeUploadLogRoot;
        sourceLabel = "Cached uploaded logs";
        if (!Directory.Exists(rootPath))
        {
            note = "Upload a logs folder once in the analyzer page before using cached raw view.";
            return false;
        }

        return true;
    }

    private static List<string> CollectRawLogFiles(string rootPath)
    {
        return Directory
            .EnumerateFiles(rootPath, "*.txt*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<List<RawLogSearchHit>> SearchRawLogsAsync(
        string rootPath,
        IEnumerable<RawLogFileOption> fileOptions,
        string searchTerm,
        CancellationToken cancellationToken)
    {
        var hits = new List<RawLogSearchHit>();
        foreach (var option in fileOptions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = Path.Combine(rootPath, option.Value.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(filePath))
            {
                continue;
            }

            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var lineNumber = 0;
            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken) ?? string.Empty;
                lineNumber++;
                if (line.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                hits.Add(new RawLogSearchHit
                {
                    Service = option.Service,
                    RelativePath = option.Value,
                    LineNumber = lineNumber,
                    LineText = line
                });

                if (hits.Count >= 500)
                {
                    return hits;
                }
            }
        }

        return hits;
    }

    private static string BuildUploadedFilePath(string rootPath, string browserRelativePath)
    {
        var segments = browserRelativePath
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(GetSafePathSegment)
            .ToArray();

        if (segments.Length == 0)
        {
            return Path.Combine(rootPath, "upload.txt");
        }

        var currentPath = rootPath;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            currentPath = Path.Combine(currentPath, segments[index]);
        }

        Directory.CreateDirectory(currentPath);
        return Path.Combine(currentPath, segments[^1]);
    }

    private static string GetSafePathSegment(string segment)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var cleaned = new string(segment.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "item" : cleaned;
    }

    private static string GetSafeFileName(string fileName, string fallbackFileName)
    {
        var safeName = GetSafePathSegment(Path.GetFileName(fileName));
        return string.IsNullOrWhiteSpace(safeName) ? fallbackFileName : safeName;
    }

    private static async Task SaveFormFileAsync(IFormFile formFile, string targetPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        using var targetStream = File.Create(targetPath);
        using var sourceStream = formFile.OpenReadStream();
        await sourceStream.CopyToAsync(targetStream, cancellationToken);
    }

    private async Task<List<HarApiRecord>> ParseHarAsync(IFormFile harFile, CancellationToken cancellationToken)
    {
        using var stream = harFile.OpenReadStream();
        return await ParseHarAsync(stream, cancellationToken);
    }

    private static async Task<List<HarApiRecord>> ParseHarAsync(Stream stream, CancellationToken cancellationToken)
    {
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var records = new List<HarApiRecord>();

        if (!document.RootElement.TryGetProperty("log", out var logElement) ||
            !logElement.TryGetProperty("entries", out var entriesElement) ||
            entriesElement.ValueKind != JsonValueKind.Array)
        {
            return records;
        }

        foreach (var entry in entriesElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("request", out var requestElement))
            {
                continue;
            }

            var record = new HarApiRecord
            {
                StartedAt = entry.TryGetProperty("startedDateTime", out var started) && started.ValueKind == JsonValueKind.String
                    ? started.GetDateTime()
                    : null,
                Method = requestElement.TryGetProperty("method", out var method) ? method.GetString() ?? "GET" : "GET",
                Url = requestElement.TryGetProperty("url", out var url) ? url.GetString() ?? string.Empty : string.Empty,
                StatusCode = entry.TryGetProperty("response", out var response) && response.TryGetProperty("status", out var status) ? status.GetInt32() : null
            };

            if (Uri.TryCreate(record.Url, UriKind.Absolute, out var uri))
            {
                record.Path = uri.AbsolutePath;
            }

            if (requestElement.TryGetProperty("headers", out var headersElement) && headersElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var header in headersElement.EnumerateArray())
                {
                    var name = header.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                    var value = header.TryGetProperty("value", out var valueValue) ? valueValue.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    if (name.Equals("correlationId", StringComparison.OrdinalIgnoreCase) || name.Equals("x-correlation-id", StringComparison.OrdinalIgnoreCase))
                    {
                        record.CorrelationId = value;
                    }
                    else if (name.Equals("traceId", StringComparison.OrdinalIgnoreCase))
                    {
                        record.TraceId = value;
                    }
                    else if (name.Equals("spanId", StringComparison.OrdinalIgnoreCase))
                    {
                        record.SpanId = value;
                    }
                    else if (name.Equals("requestId", StringComparison.OrdinalIgnoreCase) || name.Equals("request-id", StringComparison.OrdinalIgnoreCase))
                    {
                        record.RequestId = value;
                    }
                    else if (name.Equals("traceparent", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyTraceParent(record, value);
                    }
                }
            }

            records.Add(record);
        }

        return records;
    }

    private static void ApplyTraceParent(HarApiRecord record, string traceParent)
    {
        var parts = traceParent.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 4)
        {
            record.TraceId ??= parts[1];
            record.SpanId ??= parts[2];
        }
    }

    private static List<ConcurrentInsight> BuildConcurrentInsights(List<ParsedLogEntry> entries)
    {
        var insights = new List<ConcurrentInsight>();
        var errorEntries = entries.Where(entry => IsError(entry.Severity)).ToList();

        var correlatedByTrace = errorEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.TraceId))
            .GroupBy(entry => entry.TraceId!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(static entry => entry.Service).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .OrderByDescending(static group => group.Count())
            .Take(10)
            .Select(group => new ConcurrentInsight
            {
                Kind = "Cross-service trace",
                Key = group.Key,
                OccurrenceCount = group.Count(),
                Services = string.Join(", ", group.Select(static entry => entry.Service).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static value => value)),
                Summary = $"The same trace appeared across multiple services and produced {group.Count()} error line(s).",
                ExampleMessage = group.First().Message
            });

        insights.AddRange(correlatedByTrace);

        var correlatedByCorrelationId = errorEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.CorrelationId))
            .GroupBy(entry => entry.CorrelationId!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(static entry => entry.Service).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .OrderByDescending(static group => group.Count())
            .Take(10)
            .Select(group => new ConcurrentInsight
            {
                Kind = "Cross-service correlation",
                Key = group.Key,
                OccurrenceCount = group.Count(),
                Services = string.Join(", ", group.Select(static entry => entry.Service).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static value => value)),
                Summary = $"The same correlation ID surfaced in multiple services with {group.Count()} related error line(s).",
                ExampleMessage = group.First().Message
            });

        insights.AddRange(correlatedByCorrelationId);

        var repeatedSignatures = errorEntries
            .GroupBy(static entry => entry.Signature)
            .Where(group => group.Select(static entry => entry.Service).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .OrderByDescending(static group => group.Count())
            .Take(10)
            .Select(group => new ConcurrentInsight
            {
                Kind = "Repeated error signature",
                Key = group.Key,
                OccurrenceCount = group.Count(),
                Services = string.Join(", ", group.Select(static entry => entry.Service).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static value => value)),
                Summary = $"A similar error message repeated across services {group.Count()} time(s).",
                ExampleMessage = group.First().Message
            });

        insights.AddRange(repeatedSignatures);

        return insights
            .OrderByDescending(static item => item.OccurrenceCount)
            .ThenBy(static item => item.Kind)
            .ToList();
    }

    private static string ResolveServiceName(string relativePath, string? serviceHint)
    {
        if (!string.IsNullOrWhiteSpace(serviceHint))
        {
            return serviceHint.Trim();
        }

        var segments = relativePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length >= 3 && segments[0].Equals("logs", StringComparison.OrdinalIgnoreCase))
        {
            return segments[1];
        }

        if (segments.Length >= 2)
        {
            return segments[0];
        }

        return "unknown";
    }

    private static string ExtractValue(string input, string key)
    {
        var match = Regex.Match(input, $@"\b{Regex.Escape(key)}=(?<value>[^\s]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : string.Empty;
    }

    private static string NormalizeSignature(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "empty-message";
        }

        var firstLine = message.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? message;
        firstLine = Regex.Replace(firstLine, @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", "{guid}", RegexOptions.IgnoreCase);
        firstLine = Regex.Replace(firstLine, @"\b\d+\b", "{n}");
        firstLine = Regex.Replace(firstLine, @"\s{2,}", " ").Trim();
        return firstLine;
    }

    private static bool MatchesService(ParsedLogEntry entry, string? specificService)
    {
        return string.IsNullOrWhiteSpace(specificService) || entry.Service.Equals(specificService, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesSeverity(ParsedLogEntry entry, bool includeErrors, bool includeDebugInfo)
    {
        if (IsError(entry.Severity))
        {
            return includeErrors;
        }

        return includeDebugInfo;
    }

    private static bool MatchesDate(ParsedLogEntry entry, DateTime? from, DateTime? to)
    {
        if (from.HasValue && entry.Timestamp < from.Value)
        {
            return false;
        }

        if (to.HasValue && entry.Timestamp > to.Value)
        {
            return false;
        }

        return true;
    }

    private static bool MatchesIdentifier(string? value, string? explicitFilter, HashSet<string> derivedIdentifiers)
    {
        if (!string.IsNullOrWhiteSpace(explicitFilter))
        {
            return string.Equals(value, explicitFilter, StringComparison.OrdinalIgnoreCase);
        }

        if (derivedIdentifiers.Count == 0)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(value) && derivedIdentifiers.Contains(value);
    }

    private static bool MatchesKeyword(ParsedLogEntry entry, string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return true;
        }

        return entry.Message.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase)
            || entry.Signature.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase)
            || entry.RawLine.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsError(string severity)
    {
        return severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase)
            || severity.Equals("Error", StringComparison.OrdinalIgnoreCase)
            || severity.Equals("FATAL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLogEntryStart(string line)
    {
        return LogLineRegex().IsMatch(line);
    }

    private static string InferSeverity(string fileName)
    {
        if (fileName.StartsWith("errors", StringComparison.OrdinalIgnoreCase))
        {
            return "Error";
        }

        if (fileName.StartsWith("debug", StringComparison.OrdinalIgnoreCase))
        {
            return "Debug";
        }

        return "Info";
    }

    [GeneratedRegex(@"^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2},\d{3})\t\[(?<thread>[^\]]+)\]\t(?:(?<level>[A-Z]+)\t)?(?<rest>.*)$", RegexOptions.Compiled)]
    private static partial Regex LogLineRegex();

    [GeneratedRegex(@"\b(correlationId|traceId|spanId|requestId)=[^\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex IdRegex();

    private sealed class UploadSessionState
    {
        public string SessionId { get; set; } = string.Empty;

        public string LogRootPath { get; set; } = string.Empty;

        public List<string> LogFiles { get; set; } = [];

        public bool UsedSavedFiles { get; set; }
    }

    private sealed class HarValidationSourceState
    {
        public string? FilePath { get; set; }

        public string? FileName { get; set; }

        public bool UsedSavedFile { get; set; }
    }

    private sealed class HarValidationParseBundle
    {
        public string? DashboardPath { get; set; }

        public string? EnvironmentLabel { get; set; }

        public List<HarValidationEntry> Entries { get; set; } = [];
    }

    private sealed class HarValidationEntry
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

        public List<HarKeyValueItem> RequestHeaders { get; set; } = [];

        public List<HarKeyValueItem> ResponseHeaders { get; set; } = [];

        public List<HarKeyValueItem> QueryParameters { get; set; } = [];

        public string? RequestPayloadText { get; set; }

        public string? ResponseText { get; set; }

        public string? DashboardPath { get; set; }

        public string? EnvironmentLabel { get; set; }

        public bool IsLoadDashboard { get; set; }

        public bool IsApiCandidate { get; set; }
    }
}
