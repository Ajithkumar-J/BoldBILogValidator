using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using BoldLogValidator.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace BoldLogValidator.Services;

public partial class LogAnalysisService : ILogAnalysisService
{
    private const string GeneratedDashboardVersion = "16.1.90.0";
    private const int HighlightLimit = 150;
    private const int TimelinePageSize = 100;
    private const int RepeatedLogPageSize = 100;
    private const int HarApiPageSize = 100;
    private const int TimelineCacheMinutes = 20;
    private readonly string _activeUploadRoot;
    private readonly string _activeUploadLogRoot;
    private readonly string _activeUploadHarRoot;
    private readonly string _activeUploadMetadataPath;
    private readonly string _activeUploadHarMetadataPath;
    private readonly string _externalSerializationRoot;
    private readonly string _externalRuntimeRoot;
    private readonly string _externalDesignerAssetsRoot;
    private readonly IMemoryCache _memoryCache;

    public LogAnalysisService(IWebHostEnvironment environment, IMemoryCache memoryCache)
    {
        _memoryCache = memoryCache;
        _activeUploadRoot = Path.Combine(environment.ContentRootPath, "App_Data", "CurrentUpload");
        _activeUploadLogRoot = Path.Combine(_activeUploadRoot, "logs");
        _activeUploadHarRoot = Path.Combine(_activeUploadRoot, "har");
        _activeUploadMetadataPath = Path.Combine(_activeUploadRoot, "upload-session.txt");
        _activeUploadHarMetadataPath = Path.Combine(_activeUploadRoot, "har-session.txt");
        _externalSerializationRoot = Path.Combine(environment.ContentRootPath, "external", "serialization");
        _externalRuntimeRoot = Path.Combine(environment.ContentRootPath, "external", "runtime");
        _externalDesignerAssetsRoot = Path.Combine(environment.ContentRootPath, "external", "designer-assets");
        Directory.CreateDirectory(_activeUploadRoot);
        Directory.CreateDirectory(_externalSerializationRoot);
        Directory.CreateDirectory(_externalRuntimeRoot);
        Directory.CreateDirectory(_externalDesignerAssetsRoot);
    }

    public async Task<AnalysisResult> AnalyzeAsync(AnalysisFilterInput filter, CancellationToken cancellationToken = default)
    {
        var hasNewUpload = filter.LogFiles.Count > 0;
        var hasRequestedCachedUpload = !string.IsNullOrWhiteSpace(filter.UploadSessionId)
            && !string.Equals(filter.UploadSessionId, "not-uploaded-yet", StringComparison.OrdinalIgnoreCase);
        var hasCachedUploadedLogs = HasSavedUploadedLogs();
        var shouldUseLocalLogPath = !hasNewUpload
            && !hasRequestedCachedUpload
            && !hasCachedUploadedLogs
            && !string.IsNullOrWhiteSpace(filter.LocalLogPath);

        var result = new AnalysisResult
        {
            AnalysisSessionId = Guid.NewGuid().ToString("N"),
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
        CacheTimelineEntries(result.AnalysisSessionId, filteredEntries);
        var timelinePage = BuildTimelinePageResponse(filteredEntries, filter.TimelineService, filter.TimelineSortOrder, 0);
        result.TimelineEntries = timelinePage.Entries;
        result.TimelineTotalCount = timelinePage.TotalCount;
        result.TimelinePageSize = TimelinePageSize;
        result.HasMoreTimelineEntries = timelinePage.HasMore;

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

        var groupedLogSummaries = filteredEntries
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
        CacheRepeatedLogEntries(result.AnalysisSessionId, groupedLogSummaries);
        var repeatedLogPage = BuildRepeatedLogPageResponse(groupedLogSummaries, null, 0);
        result.GroupedLogSummaries = repeatedLogPage.Entries;
        result.RepeatedLogTotalCount = repeatedLogPage.TotalCount;
        result.RepeatedLogPageSize = RepeatedLogPageSize;
        result.HasMoreRepeatedLogs = repeatedLogPage.HasMore;

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
        else if (result.TimelineTotalCount > TimelinePageSize)
        {
            result.Notes.Add($"The timeline panel loads {TimelinePageSize} log lines at a time to keep large investigations responsive while you scroll.");
        }

        return result;
    }

    public Task<TimelinePageResponse> GetTimelineEntriesAsync(TimelinePageRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.AnalysisSessionId))
        {
            return Task.FromResult(new TimelinePageResponse());
        }

        if (!_memoryCache.TryGetValue(GetTimelineCacheKey(request.AnalysisSessionId), out List<ParsedLogEntry>? cachedEntries) || cachedEntries == null)
        {
            return Task.FromResult(new TimelinePageResponse());
        }

        return Task.FromResult(BuildTimelinePageResponse(cachedEntries, request.TimelineService, request.TimelineSortOrder, request.Skip));
    }

    public Task<RepeatedLogPageResponse> GetRepeatedLogEntriesAsync(RepeatedLogPageRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.AnalysisSessionId))
        {
            return Task.FromResult(new RepeatedLogPageResponse());
        }

        if (!_memoryCache.TryGetValue(GetRepeatedLogCacheKey(request.AnalysisSessionId), out List<GroupedLogSummary>? cachedEntries) || cachedEntries == null)
        {
            return Task.FromResult(new RepeatedLogPageResponse());
        }

        return Task.FromResult(BuildRepeatedLogPageResponse(cachedEntries, request.Service, request.Skip));
    }

    public async Task<HarValidationResult> GetHarValidationAsync(HarValidationFilterInput filter, CancellationToken cancellationToken = default)
    {
        var result = new HarValidationResult
        {
            AnalysisSessionId = Guid.NewGuid().ToString("N"),
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

        var filteredApis = filteredEntries
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
        CacheHarApiEntries(result.AnalysisSessionId, filteredApis);
        var harApiPage = BuildHarApiPageResponse(filteredApis, 0);
        result.FilteredApis = harApiPage.Entries;
        result.TotalFilteredApis = harApiPage.TotalCount;
        result.ApiPageSize = HarApiPageSize;
        result.HasMoreFilteredApis = harApiPage.HasMore;

        result.TotalApis = filteredApis.Count;
        result.DistinctEndpoints = filteredApis
            .Select(static entry => entry.DisplayPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        result.ErrorApis = filteredApis.Count(entry => entry.StatusCode >= 400);
        result.SlowApis = filteredApis.Count(static entry => entry.IsSlow);
        result.LoadDashboardHits = filteredApis.Count(static entry => entry.IsLoadDashboard);
        result.AverageResponseTimeMs = filteredApis.Count == 0
            ? 0
            : Math.Round(filteredApis.Average(static entry => entry.DurationMs), 1);

        var selectedEntry = filteredEntries.FirstOrDefault(entry => string.Equals(entry.Key, filter.SelectedRequestKey, StringComparison.Ordinal))
            ?? filteredEntries.FirstOrDefault(static entry => entry.IsLoadDashboard)
            ?? filteredEntries.FirstOrDefault();

        if (selectedEntry != null)
        {
            result.Filter.SelectedRequestKey = selectedEntry.Key;
        }

        result.ReconstructionInfo = BuildHarDashboardReconstructionInfo(parsedBundle, selectedEntry);

        result.StatusChips.Add("HAR parsed");
        result.StatusChips.Add("Request details on demand");

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

    public Task<HarApiPageResponse> GetHarApiEntriesAsync(HarApiPageRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.AnalysisSessionId))
        {
            return Task.FromResult(new HarApiPageResponse());
        }

        if (!_memoryCache.TryGetValue(GetHarApiCacheKey(request.AnalysisSessionId), out List<HarValidationApiItem>? cachedEntries) || cachedEntries == null)
        {
            return Task.FromResult(new HarApiPageResponse());
        }

        return Task.FromResult(BuildHarApiPageResponse(cachedEntries, request.Skip));
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

    public async Task<HarDashboardPackageExport> GenerateHarDashboardPackageAsync(
        HarValidationFilterInput filter,
        string? requestKey,
        HarDashboardExportFormat exportFormat = HarDashboardExportFormat.Zip,
        CancellationToken cancellationToken = default)
    {
        var source = await PrepareHarValidationSourceAsync(filter.HarFile, cancellationToken);
        if (string.IsNullOrWhiteSpace(source.FilePath) || !File.Exists(source.FilePath))
        {
            return new HarDashboardPackageExport
            {
                ErrorMessage = "Upload or reuse a HAR file before generating the dashboard reconstruction package."
            };
        }

        var parsedBundle = await GetCachedHarValidationBundleAsync(source.FilePath, cancellationToken);
        var selectedEntry = parsedBundle.Entries
            .Where(static entry => entry.IsApiCandidate)
            .FirstOrDefault(entry => string.Equals(entry.Key, requestKey, StringComparison.Ordinal))
            ?? parsedBundle.Entries.FirstOrDefault(static entry => entry.IsApiCandidate && entry.IsLoadDashboard)
            ?? parsedBundle.Entries.FirstOrDefault(static entry => entry.IsApiCandidate);

        if (selectedEntry == null)
        {
            return new HarDashboardPackageExport
            {
                ErrorMessage = "No matching API request was found in the current HAR bundle."
            };
        }

        if (!selectedEntry.IsLoadDashboard)
        {
            selectedEntry = parsedBundle.Entries.FirstOrDefault(static entry => entry.IsApiCandidate && entry.IsLoadDashboard) ?? selectedEntry;
        }

        if (!selectedEntry.IsLoadDashboard)
        {
            return new HarDashboardPackageExport
            {
                ErrorMessage = "The current HAR file does not contain a LoadDashboard API response that can be reconstructed."
            };
        }

        var packageData = TryExtractDashboardPackageData(selectedEntry.ResponseText, out var extractionError);
        if (packageData == null)
        {
            return new HarDashboardPackageExport
            {
                ErrorMessage = extractionError ?? "Unable to decode the selected LoadDashboard response into a dashboard reconstruction package."
            };
        }

        var serializationDlls = Directory.Exists(_externalSerializationRoot)
            ? Directory.GetFiles(_externalSerializationRoot, "*.dll", SearchOption.TopDirectoryOnly)
            : [];
        NormalizePortableDatasourceProviders(packageData);
        var schemaReport = BuildDatasourceSchemaReport(packageData.SourceDashboardJson, packageData.WidgetData);
        var sqlScripts = BuildDatasourceBootstrapScripts(schemaReport);

        if (exportFormat == HarDashboardExportFormat.Bbix)
        {
            var bbixContent = BuildBbixContent(packageData, schemaReport);
            return new HarDashboardPackageExport
            {
                Success = true,
                FileName = BuildDashboardBbixFileName(packageData, parsedBundle, source.FileName),
                ContentType = "application/json",
                Content = Encoding.UTF8.GetBytes(bbixContent)
            };
        }

        var packageFileName = BuildDashboardPackageFileName(packageData, parsedBundle, source.FileName);

        await using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddZipEntry(archive, "dashboard.json", SerializeJson(packageData.DashboardJson));
            AddZipEntry(archive, "widgetdata.json", SerializeJson(packageData.WidgetData));
            AddZipEntry(archive, "filterdata.json", SerializeJson(packageData.FilterData));
            AddZipEntry(archive, "colorset.json", SerializeJson(packageData.ColorSetData));
            AddZipEntry(archive, "manifest.json", BuildPackageManifestJson(packageData, selectedEntry, parsedBundle, source.FileName, schemaReport, serializationDlls.Length));
            AddZipEntry(archive, "readme.txt", BuildPackageReadme(packageData, selectedEntry, schemaReport, serializationDlls.Length));
            AddZipEntry(archive, "datasource-schema.json", SerializeJson(schemaReport.ReportJson));
            AddZipEntry(archive, "load-dashboard-context.json", SerializeJson(packageData.ContextJson));

            foreach (var sqlScript in sqlScripts)
            {
                AddZipEntry(archive, $"database/{sqlScript.FileName}", sqlScript.Content);
            }
        }

        return new HarDashboardPackageExport
        {
            Success = true,
            FileName = packageFileName,
            Content = memoryStream.ToArray()
        };
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

    private HarDashboardReconstructionInfo BuildHarDashboardReconstructionInfo(HarValidationParseBundle parsedBundle, HarValidationEntry? selectedEntry)
    {
        var serializationLocation = ResolveIntegrationLocation(
            _externalSerializationRoot,
            [
                @"D:\Dev-run-V2\dashboard-designer-web-serialization\bin",
                @"D:\Dev-run-V2\dashboard-designer-web-serialization"
            ],
            "*.dll");
        var runtimeLocation = ResolveIntegrationLocation(
            _externalRuntimeRoot,
            [
                @"C:\BoldServices\bi\dataservice"
            ],
            "*.dll");
        var designerAssetsLocation = ResolveIntegrationLocation(
            _externalDesignerAssetsRoot,
            [
                @"D:\Dev-run-V2\dashboard-designer-web-designer\assets"
            ],
            "*.*");
        var hasLoadDashboard = parsedBundle.Entries.Any(static entry => entry.IsApiCandidate && entry.IsLoadDashboard);
        var selectedIsLoadDashboard = selectedEntry?.IsLoadDashboard == true;
        var canGenerateBbix = hasLoadDashboard;

        var statusNote = hasLoadDashboard
            ? selectedIsLoadDashboard
                ? "The selected request is a LoadDashboard API. ZIP reconstruction can be generated directly from this response."
                : "A LoadDashboard API is present in this HAR. ZIP reconstruction will use the selected request if it is LoadDashboard, otherwise it falls back to the first detected LoadDashboard response."
            : "No LoadDashboard API was found in the current HAR file. Reconstruction stays unavailable until one is present.";

        var bbixStatusNote = canGenerateBbix
            ? "BBIX generation is enabled from the HAR-derived persisted dashboard model. Validate the output in Bold BI and use the generated SQL/schema helpers to recreate datasource structure before upload."
            : "BBIX generation stays unavailable until a LoadDashboard API is present in the HAR.";

        return new HarDashboardReconstructionInfo
        {
            HasLoadDashboardApi = hasLoadDashboard,
            SelectedRequestIsLoadDashboard = selectedIsLoadDashboard,
            CanGeneratePackage = hasLoadDashboard,
            CanGenerateBbix = canGenerateBbix,
            ExtractionMode = serializationLocation.FileCount > 0
                ? "Fallback JSON parsing with latest serialization DLLs detected"
                : "Fallback JSON parsing",
            SerializationFolderPath = serializationLocation.DisplayPath,
            SerializationAssemblyCount = serializationLocation.FileCount,
            RuntimeFolderPath = runtimeLocation.DisplayPath,
            RuntimeAssemblyCount = runtimeLocation.FileCount,
            DesignerAssetsFolderPath = designerAssetsLocation.DisplayPath,
            DesignerAssetsDetected = designerAssetsLocation.Exists,
            StatusNote = statusNote,
            BbixStatusNote = bbixStatusNote,
            PackageContents =
            [
                "dashboard.json",
                "widgetdata.json",
                "filterdata.json",
                "colorset.json",
                "manifest.json",
                "datasource-schema.json",
                "database/*.sql",
                "readme.txt"
            ]
        };
    }

    private static IntegrationLocation ResolveIntegrationLocation(string preferredPath, IEnumerable<string> fallbackCandidates, string searchPattern)
    {
        if (Directory.Exists(preferredPath))
        {
            var preferredCount = Directory.GetFiles(preferredPath, searchPattern, SearchOption.TopDirectoryOnly).Length;
            if (preferredCount > 0 || !fallbackCandidates.Any())
            {
                return new IntegrationLocation(preferredPath, preferredCount, true);
            }
        }

        foreach (var candidate in fallbackCandidates)
        {
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            var count = Directory.GetFiles(candidate, searchPattern, SearchOption.TopDirectoryOnly).Length;
            if (count > 0)
            {
                return new IntegrationLocation(candidate, count, true);
            }
        }

        return new IntegrationLocation(preferredPath, 0, Directory.Exists(preferredPath));
    }

    private static DashboardPackageData? TryExtractDashboardPackageData(string? responseText, out string? errorMessage)
    {
        errorMessage = null;
        var responseNode = ParseJsonNode(responseText);
        if (responseNode == null)
        {
            errorMessage = "The LoadDashboard response did not contain parseable JSON content.";
            return null;
        }

        var outerObject = responseNode as JsonObject;
        var contextNode = TryResolveNestedJsonNode(outerObject?["Data"]) ?? responseNode;
        var contextObject = contextNode as JsonObject;
        var dashboardNode = TryResolveNestedJsonNode(contextObject?["DashboardData"]) ?? contextNode;
        if (dashboardNode is not JsonObject dashboardObject)
        {
            errorMessage = "The LoadDashboard response did not expose a DashboardData object.";
            return null;
        }

        var sourceDashboardJson = dashboardObject.DeepClone() as JsonObject ?? new JsonObject();
        var widgetData = CloneOrDefaultArray(sourceDashboardJson["Widgets"]);
        var filterData = CloneOrDefaultNode(sourceDashboardJson["MasterFilterInfo"], CreateDefaultFilterData());
        var colorSetData = CloneOrDefaultArray(sourceDashboardJson["ColorSets"]);
        var dashboardJson = BuildPersistedDashboardJson(sourceDashboardJson);

        return new DashboardPackageData
        {
            DashboardJson = dashboardJson,
            SourceDashboardJson = sourceDashboardJson,
            WidgetData = widgetData,
            FilterData = filterData,
            ColorSetData = colorSetData,
            ContextJson = contextObject?.DeepClone() as JsonObject ?? new JsonObject(),
            DashboardPath = dashboardObject["DashboardPath"]?.GetValue<string>(),
            DashboardId = dashboardObject["DashboardId"]?.GetValue<string>(),
            DashboardObjectId = dashboardObject["DashboardObjectId"]?.GetValue<string>()
        };
    }

    private static JsonNode? TryResolveNestedJsonNode(JsonNode? source)
    {
        if (source == null)
        {
            return null;
        }

        if (source is JsonValue valueNode && valueNode.TryGetValue<string>(out var stringValue))
        {
            return ParseJsonNode(stringValue) ?? JsonValue.Create(stringValue);
        }

        return source.DeepClone();
    }

    private static JsonArray CloneOrDefaultArray(JsonNode? node)
    {
        if (node is JsonArray arrayNode)
        {
            return arrayNode.DeepClone() as JsonArray ?? [];
        }

        var resolved = TryResolveNestedJsonNode(node);
        return resolved as JsonArray ?? [];
    }

    private static JsonNode CloneOrDefaultNode(JsonNode? node, JsonNode defaultValue)
    {
        var resolved = TryResolveNestedJsonNode(node);
        return resolved ?? defaultValue.DeepClone() ?? defaultValue;
    }

    private static JsonObject CreateDefaultFilterData()
    {
        return new JsonObject
        {
            ["MasterSlaveFilterActions"] = new JsonArray(),
            ["ParameterMappingFilterActions"] = null
        };
    }

    private static JsonObject BuildPersistedDashboardJson(JsonObject sourceDashboardJson)
    {
        var dashboardProperties = sourceDashboardJson["DashboardProperties"] as JsonObject ?? new JsonObject();
        var dashboardJson = new JsonObject
        {
            ["id"] = sourceDashboardJson["DashboardObjectId"]?.GetValue<string>() ?? sourceDashboardJson["DashboardId"]?.GetValue<string>() ?? Guid.NewGuid().ToString(),
            ["name"] = dashboardProperties["Name"]?.GetValue<string>() ?? sourceDashboardJson["DashboardPath"]?.GetValue<string>() ?? "Reconstructed Dashboard",
            ["description"] = dashboardProperties["Description"]?.GetValue<string>() ?? string.Empty,
            ["enableComment"] = dashboardProperties["EnableComments"]?.GetValue<bool?>(),
            ["enableMetrics"] = dashboardProperties["EnableMetrics"]?.GetValue<bool?>(),
            ["enableSkeletonLoading"] = dashboardProperties["EnableSkeletonLoading"]?.GetValue<bool?>(),
            ["widgetProgress"] = ConvertPropertyNode(dashboardProperties["WidgetProgress"]),
            ["showWidgetCellCount"] = dashboardProperties["ShowWidgetCellCount"]?.GetValue<bool?>(),
            ["dashboardFontInfo"] = ConvertPropertyNode(dashboardProperties["DashboardFontInfo"]),
            ["widgetFontInfo"] = ConvertPropertyNode(dashboardProperties["WidgetFontInfo"]),
            ["connections"] = BuildPersistedConnections(sourceDashboardJson["DataSources"] as JsonArray),
            ["datasources"] = BuildPersistedDatasources(sourceDashboardJson["DataSets"] as JsonArray),
            ["cacheSettingInfo"] = NormalizeCacheSettingInfo(sourceDashboardJson["cacheSettingInfo"]),
            ["autoRefreshConfiguration"] = ConvertPropertyNode(sourceDashboardJson["RefreshSettingInfo"]) ?? new JsonObject(),
            ["dashboardVersion"] = GeneratedDashboardVersion,
            ["parameterColumns"] = ConvertPropertyNode(sourceDashboardJson["DashboardParamList"]) ?? new JsonArray(),
            ["isConnectionEncrypted"] = true,
            ["useCommonEncryption"] = true,
            ["designCanvasSettings"] = ConvertPropertyNode(sourceDashboardJson["DesignCanvasSettings"]),
            ["dynamicLocalization"] = ConvertPropertyNode(sourceDashboardJson["DynamicLocalization"]) ?? new JsonArray(),
            ["dashboardExporting"] = ConvertPropertyNode(dashboardProperties["DashboardExport"]),
            ["canvasStyleChanging"] = BuildCanvasStyleChanging(dashboardProperties["CanvasStyle"]),
            ["bannerPanelStyleChanging"] = BuildBannerPanelStyleChanging(dashboardProperties["BannerPanelStyle"]),
            ["aiPoweredSummarizationChanging"] = ConvertPropertyNode(dashboardProperties["AiPoweredSummarization"]),
            ["aICustomPrompt"] = BuildAiCustomPrompt(dashboardProperties["AiPoweredSummarization"]),
            ["isDataSamplingEnabled"] = dashboardProperties["IsDataSampleEnabled"]?.GetValue<bool?>(),
            ["isThresHoldEnabled"] = dashboardProperties["IsThresHoldEnabled"]?.GetValue<bool?>(),
            ["layoutSize"] = BuildLayoutSize(dashboardProperties["LayoutSize"]),
            ["designBasicSettings"] = ConvertPropertyNode(dashboardProperties["DesignBasicSettings"]),
            ["globalFontFamily"] = dashboardProperties["GlobalFontFamily"]?.GetValue<string>(),
            ["isGlobalAutoFontFamily"] = dashboardProperties["IsGlobalAutoFontFamily"]?.GetValue<bool?>(),
            ["globalFontStyle"] = ConvertPropertyNode(dashboardProperties["GlobalFontStyle"]) ?? new JsonObject()
        };

        RemoveNullProperties(dashboardJson);
        return dashboardJson;
    }

    private static JsonArray BuildPersistedConnections(JsonArray? sourceDataSources)
    {
        var connections = new JsonArray();
        if (sourceDataSources == null)
        {
            return connections;
        }

        foreach (var datasource in sourceDataSources.OfType<JsonObject>())
        {
            var providerType = datasource["ProviderType"]?.GetValue<string>()
                ?? datasource["ConnectionProperties"]?["DataProvider"]?.GetValue<string>()
                ?? "PostgreSQL";
            var providerKey = NormalizeProviderKey(providerType);
            var connectionProperties = datasource["ConnectionProperties"] as JsonObject;
            var connection = new JsonObject
            {
                ["$type"] = GetConnectionTypeName(providerKey),
                ["id"] = datasource["Id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                ["name"] = datasource["Name"]?.GetValue<string>() ?? "Datasource",
                ["pluginUID"] = GetPluginUid(providerKey),
                ["InitialDataSourceVersion"] = GeneratedDashboardVersion,
                ["isConnectionEncryption"] = true
            };

            switch (providerKey)
            {
                case "sqlserver":
                    connection["datasource"] = BuildSqlServerDatasourceAddress(connectionProperties, datasource);
                    connection["initialCatalog"] = ReadFirstString(connectionProperties?["Database"], datasource["Database"]);
                    connection["username"] = ReadFirstString(connectionProperties?["UserName"], datasource["Username"]);
                    connection["password"] = ReadFirstString(connectionProperties?["PassWord"], datasource["Password"]);
                    connection["commandTimeout"] = ReadFirstString(connectionProperties?["CommandTimeout"], datasource["CommandTimeout"]) ?? "300";
                    connection["advancedSettings"] = SerializeScalarNode(connectionProperties?["AdvancedSettings"]);
                    connection["connectiontype"] = ReadFirstString(connectionProperties?["ConnectionType"], datasource["ConnectionType"]) ?? string.Empty;
                    break;
                default:
                    connection["serverName"] = ReadFirstString(connectionProperties?["ServerName"], datasource["ServerName"]);
                    connection["userName"] = ReadFirstString(connectionProperties?["UserName"], datasource["Username"]);
                    connection["password"] = ReadFirstString(connectionProperties?["PassWord"], datasource["Password"]);
                    connection["portNumber"] = ReadFirstString(connectionProperties?["Port"], datasource["Port"]);
                    connection["database"] = ReadFirstString(connectionProperties?["Database"], datasource["Database"]);
                    connection["commandTimeout"] = ReadFirstString(connectionProperties?["CommandTimeout"], datasource["CommandTimeout"]) ?? "300";
                    connection["advancedSettings"] = SerializeScalarNode(connectionProperties?["AdvancedSettings"]);
                    connection["sslMode"] = BuildSslMode(datasource, connectionProperties);
                    connection["connectiontype"] = ReadFirstString(connectionProperties?["ConnectionType"], datasource["ConnectionType"]) ?? string.Empty;
                    break;
            }

            RemoveNullProperties(connection);
            connections.Add(connection);
        }

        return connections;
    }

    private static JsonArray BuildPersistedDatasources(JsonArray? sourceDataSets)
    {
        var datasources = new JsonArray();
        if (sourceDataSets == null)
        {
            return datasources;
        }

        foreach (var dataset in sourceDataSets.OfType<JsonObject>())
        {
            datasources.Add(BuildPersistedDatasource(dataset));
        }

        return datasources;
    }

    private static JsonObject BuildPersistedDatasource(JsonObject dataset)
    {
        var datasourceId = dataset["Id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
        var tableIdMap = BuildTableIdMap(dataset);
        var expressionLookup = BuildExpressionLookup(dataset["Expressions"] as JsonArray);
        var tables = BuildPersistedTables(dataset, datasourceId, tableIdMap, expressionLookup);
        var mainFilters = ConvertPropertyNode(dataset["InitialFilterInfo"]) ?? new JsonArray();
        var relationshipInfo = ConvertPropertyNode(dataset["RelationshipModelInfo"]) ?? new JsonArray();
        var customHierarchy = ConvertPropertyNode(dataset["CustomHierarchyFields"]) ?? new JsonArray();
        var dynamicParameters = ConvertPropertyNode(dataset["QueryParameters"]) ?? new JsonArray();
        var expressions = BuildPersistedExpressions(dataset, datasourceId, tableIdMap, expressionLookup);

        var result = new JsonObject
        {
            ["id"] = datasourceId,
            ["name"] = dataset["Name"]?.GetValue<string>() ?? datasourceId,
            ["description"] = dataset["Description"]?.GetValue<string>() ?? string.Empty,
            ["tables"] = tables,
            ["selectedTableInfo"] = ConvertPropertyNode(dataset["SelectedTableInfo"]) ?? new JsonArray(),
            ["expressions"] = expressions,
            ["mainFilters"] = mainFilters,
            ["tableRelations"] = BuildTableRelations(dataset["Join"] as JsonArray),
            ["relationshipInfo"] = relationshipInfo,
            ["customHierarchy"] = customHierarchy,
            ["dynamicparameters"] = dynamicParameters,
            ["publishId"] = dataset["PublishId"]?.GetValue<string>(),
            ["analyticDashboard"] = "None",
            ["dataSamplingInfo"] = BuildSamplingInfo(dataset["DataSamplingInfo"]),
            ["thresHoldInfo"] = BuildThresholdInfo(dataset["ThresHoldInfo"])
        };

        RemoveNullProperties(result);
        return result;
    }

    private static Dictionary<string, string> BuildExpressionLookup(JsonArray? expressions)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (expressions == null)
        {
            return lookup;
        }

        foreach (var expression in expressions.OfType<JsonObject>())
        {
            var name = expression["Name"]?.GetValue<string>();
            var queryExp = expression["QueryExp"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(queryExp) && !lookup.ContainsKey(name))
            {
                lookup[name] = queryExp;
            }
        }

        return lookup;
    }

    private static Dictionary<string, string> BuildTableIdMap(JsonObject dataset)
    {
        var tableIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fields = dataset["Fields"] as JsonArray;
        var joins = dataset["Join"] as JsonArray;

        if (joins != null)
        {
            foreach (var joinObject in joins.OfType<JsonObject>())
            {
                var joinFields = joinObject["JoinFields"] as JsonArray;
                if (joinFields == null)
                {
                    continue;
                }

                foreach (var joinField in joinFields.OfType<JsonObject>())
                {
                    MapJoinFieldTableId(tableIdMap, fields, joinField["LeftField"]?.GetValue<string>(), joinObject["LeftTable"]?.GetValue<string>());
                    MapJoinFieldTableId(tableIdMap, fields, joinField["RightField"]?.GetValue<string>(), joinObject["RightTable"]?.GetValue<string>());
                }
            }
        }

        if (fields != null)
        {
            foreach (var field in fields.OfType<JsonObject>())
            {
                var tableName = field["TableName"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(tableName) || tableIdMap.ContainsKey(tableName))
                {
                    continue;
                }

                tableIdMap[tableName] = BuildDeterministicId($"table:{dataset["Id"]?.GetValue<string>()}:{tableName}");
            }
        }

        return tableIdMap;
    }

    private static void MapJoinFieldTableId(Dictionary<string, string> tableIdMap, JsonArray? fields, string? fieldId, string? tableId)
    {
        if (fields == null || string.IsNullOrWhiteSpace(fieldId) || string.IsNullOrWhiteSpace(tableId))
        {
            return;
        }

        var field = fields.OfType<JsonObject>()
            .FirstOrDefault(item => string.Equals(item["Id"]?.GetValue<string>(), fieldId, StringComparison.OrdinalIgnoreCase));
        var tableName = field?["TableName"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(tableName) && !tableIdMap.ContainsKey(tableName))
        {
            tableIdMap[tableName] = tableId;
        }
    }

    private static JsonArray BuildPersistedTables(JsonObject dataset, string datasourceId, Dictionary<string, string> tableIdMap, IReadOnlyDictionary<string, string> expressionLookup)
    {
        var tables = new JsonArray();
        var fields = dataset["Fields"] as JsonArray;
        if (fields == null)
        {
            return tables;
        }

        foreach (var fieldGroup in fields
                     .OfType<JsonObject>()
                     .Where(static item => !string.IsNullOrWhiteSpace(item["TableName"]?.GetValue<string>()))
                     .GroupBy(item => item["TableName"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase))
        {
            var tableName = fieldGroup.Key;
            var tableId = tableIdMap.TryGetValue(tableName, out var mappedTableId)
                ? mappedTableId
                : BuildDeterministicId($"table:{datasourceId}:{tableName}");
            var tableNode = new JsonObject
            {
                ["id"] = tableId,
                ["name"] = tableName,
                ["connection"] = datasourceId,
                ["fields"] = new JsonArray(fieldGroup.Select(field => (JsonNode)BuildPersistedField(field, datasourceId, tableId, tableName, expressionLookup)).ToArray()),
                ["bounds"] = new JsonObject(),
                ["schema"] = ResolveTableSchema(tableName),
                ["sourceId"] = string.Empty,
                ["alias"] = tableName
            };

            tables.Add(tableNode);
        }

        return tables;
    }

    private static JsonArray BuildPersistedExpressions(JsonObject dataset, string datasourceId, IReadOnlyDictionary<string, string> tableIdMap, IReadOnlyDictionary<string, string> expressionLookup)
    {
        var expressions = new JsonArray();
        var fields = dataset["Fields"] as JsonArray;
        if (fields == null)
        {
            return expressions;
        }

        foreach (var sourceField in fields.OfType<JsonObject>().Where(static field => field["IsExpression"]?.GetValue<bool?>() == true))
        {
            expressions.Add(BuildPersistedExpression(sourceField, datasourceId, tableIdMap, expressionLookup));
        }

        return expressions;
    }

    private static JsonObject BuildPersistedExpression(JsonObject sourceField, string datasourceId, IReadOnlyDictionary<string, string> tableIdMap, IReadOnlyDictionary<string, string> expressionLookup)
    {
        var tableName = sourceField["TableName"]?.GetValue<string>() ?? string.Empty;
        var tableId = !string.IsNullOrWhiteSpace(tableName) && tableIdMap.TryGetValue(tableName, out var mappedTableId)
            ? mappedTableId
            : BuildDeterministicId($"table:{datasourceId}:{tableName}");
        var tableIdentifier = string.IsNullOrWhiteSpace(tableName) ? string.Empty : $"{datasourceId}.{tableName}";
        var queryFieldId = ResolveQueryFieldId(sourceField);

        return new JsonObject
        {
            ["table"] = tableIdentifier,
            ["id"] = sourceField["Id"]?.GetValue<string>() ?? BuildDeterministicId($"expression:{datasourceId}:{tableName}:{queryFieldId}"),
            ["queryField"] = BuildPersistedQueryField(sourceField, tableId, expressionLookup),
            ["fullId"] = string.IsNullOrWhiteSpace(tableIdentifier) ? queryFieldId : $"{tableIdentifier}.{queryFieldId}",
            ["filterranklimit"] = int.MaxValue,
            ["PoPFilters"] = new JsonArray(),
            ["synonyms"] = string.Empty,
            ["valueSynonyms"] = string.Empty,
            ["IsDashboardExpression"] = sourceField["IsDashboardExpression"]?.GetValue<bool?>() ?? false
        };
    }

    private static JsonObject BuildPersistedField(JsonObject sourceField, string datasourceId, string tableId, string tableName, IReadOnlyDictionary<string, string> expressionLookup)
    {
        var queryField = BuildPersistedQueryField(sourceField, tableId, expressionLookup);

        return new JsonObject
        {
            ["table"] = $"{datasourceId}.{tableName}",
            ["id"] = sourceField["Id"]?.GetValue<string>() ?? BuildDeterministicId($"field:{datasourceId}:{tableName}:{sourceField["Name"]?.GetValue<string>()}"),
            ["queryField"] = queryField,
            ["filterranklimit"] = int.MaxValue,
            ["PoPFilters"] = new JsonArray(),
            ["synonyms"] = string.Empty,
            ["valueSynonyms"] = string.Empty
        };
    }

    private static JsonObject BuildPersistedQueryField(JsonObject sourceField, string tableId, IReadOnlyDictionary<string, string> expressionLookup)
    {
        var typeName = sourceField["TypeName"]?.GetValue<string>();
        var queryFieldId = ResolveQueryFieldId(sourceField);
        var alias = sourceField["Name"]?.GetValue<string>() ?? queryFieldId;
        var queryField = new JsonObject
        {
            ["id"] = queryFieldId,
            ["table"] = tableId,
            ["columnname"] = sourceField["DataField"]?.GetValue<string>() ?? alias,
            ["defaultcolumnname"] = sourceField["DataField"]?.GetValue<string>() ?? alias,
            ["isInResult"] = true,
            ["alias"] = alias,
            ["type"] = new JsonObject
            {
                ["type"] = MapFieldTypeCode(typeName),
                ["connectiontype"] = MapConnectionTypeName(typeName)
            }
        };

        var conversion = BuildFieldConversion(typeName);
        if (conversion != null)
        {
            queryField["conversion"] = conversion;
        }

        if (sourceField["IsExpression"]?.GetValue<bool?>() == true)
        {
            queryField["isExpressionField"] = true;
            queryField["customExpression"] = BuildPersistedCustomExpression(sourceField, expressionLookup);
        }

        return queryField;
    }

    private static JsonObject BuildPersistedCustomExpression(JsonObject sourceField, IReadOnlyDictionary<string, string> expressionLookup)
    {
        var expressionText = ResolveExpressionText(sourceField, expressionLookup);
        return new JsonObject
        {
            ["expression"] = expressionText,
            ["displayText"] = expressionText,
            ["formattedExpression"] = expressionText,
            ["hasFieldName"] = expressionText.Contains('[', StringComparison.Ordinal) || expressionText.Contains('{', StringComparison.Ordinal),
            ["isAggregation"] = sourceField["IsAggregatedExpression"]?.GetValue<bool?>() ?? false,
            ["isCustomExpression"] = true,
            ["hasVariableParameter"] = expressionText.Contains('@', StringComparison.Ordinal),
            ["isWindowExpression"] = false,
            ["isLODExpression"] = false
        };
    }

    private static string ResolveExpressionText(JsonObject sourceField, IReadOnlyDictionary<string, string> expressionLookup)
    {
        foreach (var key in new[]
                 {
                     sourceField["Name"]?.GetValue<string>(),
                     sourceField["DataField"]?.GetValue<string>(),
                     sourceField["Id"]?.GetValue<string>()
                 })
        {
            if (!string.IsNullOrWhiteSpace(key) && expressionLookup.TryGetValue(key, out var expressionText) && !string.IsNullOrWhiteSpace(expressionText))
            {
                return expressionText;
            }
        }

        return sourceField["Name"]?.GetValue<string>()
            ?? sourceField["DataField"]?.GetValue<string>()
            ?? sourceField["Id"]?.GetValue<string>()
            ?? string.Empty;
    }

    private static string ResolveQueryFieldId(JsonObject sourceField)
    {
        return sourceField["DataField"]?.GetValue<string>()
            ?? sourceField["Name"]?.GetValue<string>()
            ?? sourceField["Id"]?.GetValue<string>()
            ?? Guid.NewGuid().ToString("N");
    }

    private static JsonArray BuildTableRelations(JsonArray? joins)
    {
        var relations = new JsonArray();
        if (joins == null)
        {
            return relations;
        }

        foreach (var join in joins.OfType<JsonObject>())
        {
            relations.Add(new JsonObject
            {
                ["joinOn"] = new JsonObject
                {
                    ["conditions"] = ConvertJoinConditions(join["JoinFields"] as JsonArray)
                },
                ["leftTableIdentifier"] = join["LeftTable"]?.GetValue<string>(),
                ["rightTableIdentifier"] = join["RightTable"]?.GetValue<string>(),
                ["type"] = MapJoinType(join["JoinType"]?.GetValue<string>())
            });
        }

        return relations;
    }

    private static JsonArray ConvertJoinConditions(JsonArray? joinFields)
    {
        var conditions = new JsonArray();
        if (joinFields == null)
        {
            return conditions;
        }

        foreach (var joinField in joinFields.OfType<JsonObject>())
        {
            conditions.Add(new JsonObject
            {
                ["subElements"] = new JsonArray(),
                ["element"] = new JsonObject
                {
                    ["field1"] = BuildJoinFieldReference(joinField["LeftField"]?.GetValue<string>()),
                    ["field2"] = BuildJoinFieldReference(joinField["RightField"]?.GetValue<string>()),
                    ["operator"] = MapJoinFunctionType(joinField["OperatorType"]?.GetValue<string>()),
                    ["isValueBased"] = joinField["IsValueChecked"]?.GetValue<bool?>() ?? false,
                    ["value"] = joinField["Value"]?.GetValue<string>() ?? string.Empty
                },
                ["operator"] = MapAndOrOperator(joinField["Condition"]?.GetValue<string>())
            });
        }

        return conditions;
    }

    private static JsonObject BuildJoinFieldReference(string? fieldId)
    {
        return new JsonObject
        {
            ["field"] = fieldId ?? string.Empty,
            ["id"] = fieldId ?? string.Empty
        };
    }

    private static string MapJoinType(string? joinType)
    {
        return joinType switch
        {
            "Left Outer Join" => "LeftOuterJoin",
            "Right Outer Join" => "RightOuterJoin",
            "Full Outer Join" => "FullOuterJoin",
            "Cross Join" => "CrossJoin",
            _ => "InnerJoin"
        };
    }

    private static string MapJoinFunctionType(string? operatorType)
    {
        return operatorType switch
        {
            "<=" => "LESSOREQUALS",
            ">=" => "GREATEROREQUALS",
            "!=" => "NOTEQUALS",
            _ => "ISEQUAL"
        };
    }

    private static string MapAndOrOperator(string? condition)
    {
        return condition?.ToUpperInvariant() switch
        {
            "OR" => "Or",
            "OR NOT" => "OrNot",
            "AND NOT" => "AndNot",
            _ => "And"
        };
    }

    private static JsonNode? ConvertPropertyNode(JsonNode? node)
    {
        return node switch
        {
            null => null,
            JsonObject obj => ConvertPropertyObject(obj),
            JsonArray arr => new JsonArray(arr.Select(ConvertPropertyNode).ToArray()),
            JsonValue value => value.DeepClone(),
            _ => node.DeepClone()
        };
    }

    private static JsonObject ConvertPropertyObject(JsonObject source)
    {
        var target = new JsonObject();
        foreach (var property in source)
        {
            target[ToCamelCase(property.Key)] = ConvertPropertyNode(property.Value);
        }

        return target;
    }

    private static string ToCamelCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !char.IsUpper(value[0]))
        {
            return value;
        }

        if (value.Length > 1 && char.IsUpper(value[1]))
        {
            return char.ToLowerInvariant(value[0]) + value[1..];
        }

        return char.ToLowerInvariant(value[0]) + value[1..];
    }

    private static JsonNode? BuildCanvasStyleChanging(JsonNode? canvasStyleNode)
    {
        var canvasStyle = ConvertPropertyNode(canvasStyleNode) as JsonObject;
        if (canvasStyle == null)
        {
            return null;
        }

        canvasStyle.Remove("enableBackgroundColor");
        if (canvasStyle["imageInfo"] is JsonObject imageInfo)
        {
            imageInfo.Remove("enableBackgroundImage");
            imageInfo.Remove("imageBase64");
        }

        return canvasStyle;
    }

    private static JsonNode? BuildBannerPanelStyleChanging(JsonNode? bannerPanelStyleNode)
    {
        var bannerStyle = ConvertPropertyNode(bannerPanelStyleNode) as JsonObject;
        if (bannerStyle == null)
        {
            return null;
        }

        bannerStyle.Remove("enableBackgroundColor");
        bannerStyle.Remove("enableForegroundColor");
        return bannerStyle;
    }

    private static JsonNode BuildAiCustomPrompt(JsonNode? summarizationNode)
    {
        return new JsonObject
        {
            ["CustomPrompt"] = string.Empty,
            ["Description"] = string.Empty,
            ["IsEmojisRequired"] = true,
            ["MaximumSummaryLength"] = 2000,
            ["SummaryEnabled"] = summarizationNode?["DashboardSummary"]?.GetValue<bool?>()
        };
    }

    private static JsonNode? BuildLayoutSize(JsonNode? layoutSizeNode)
    {
        var layout = ConvertPropertyNode(layoutSizeNode) as JsonObject;
        if (layout == null)
        {
            return null;
        }

        if (layout["type"] is JsonValue typeValue && typeValue.TryGetValue<int>(out var type))
        {
            layout["type"] = type == 0 ? "Automatic" : type.ToString(CultureInfo.InvariantCulture);
        }

        return layout;
    }

    private static JsonNode? BuildSamplingInfo(JsonNode? samplingNode)
    {
        var sampling = ConvertPropertyNode(samplingNode) as JsonObject;
        if (sampling == null)
        {
            return null;
        }

        if (sampling["dataLimit"] != null)
        {
            sampling["datalimit"] = sampling["dataLimit"]!.DeepClone();
            sampling.Remove("dataLimit");
        }

        return sampling;
    }

    private static JsonNode? BuildThresholdInfo(JsonNode? thresholdNode)
    {
        return ConvertPropertyNode(thresholdNode);
    }

    private static JsonNode? NormalizeCacheSettingInfo(JsonNode? cacheSettingNode)
    {
        var cacheSettings = ConvertPropertyNode(cacheSettingNode) as JsonObject;
        if (cacheSettings == null)
        {
            return null;
        }

        if (cacheSettings["cacheMode"] is JsonValue cacheModeValue &&
            cacheModeValue.TryGetValue<string>(out var cacheMode) &&
            !string.IsNullOrWhiteSpace(cacheMode))
        {
            cacheSettings["cacheMode"] = cacheMode.Trim() switch
            {
                "In-Memory" => "InMemory",
                "Files System" => "FilesSystem",
                _ => cacheMode.Trim()
            };
        }

        return cacheSettings;
    }

    private static string BuildDeterministicId(string value)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static int MapFieldTypeCode(string? typeName)
    {
        return NormalizeTypeName(typeName) switch
        {
            "boolean" => 0,
            "integer" => 1,
            "real" => 2,
            "date" => 3,
            "datetime" => 3,
            _ => 4
        };
    }

    private static string MapConnectionTypeName(string? typeName)
    {
        return NormalizeTypeName(typeName) switch
        {
            "boolean" => "boolean",
            "integer" => "integer",
            "real" => "numeric",
            "date" => "date",
            "datetime" => "timestamp",
            _ => "text"
        };
    }

    private static JsonNode? BuildFieldConversion(string? typeName)
    {
        return NormalizeTypeName(typeName) switch
        {
            "date" => new JsonObject { ["outputType"] = 3 },
            "datetime" => new JsonObject { ["outputType"] = 3 },
            _ => null
        };
    }

    private static string ResolveTableSchema(string tableName)
    {
        if (tableName.Contains('.', StringComparison.Ordinal))
        {
            return tableName.Split('.', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        }

        return "public";
    }

    private static string GetConnectionTypeName(string providerKey)
    {
        return providerKey switch
        {
            "sqlserver" => "Syncfusion.Dashboard.Connection.SQLServer.Json.JsonSQLConnection, Syncfusion.Dashboard.Connection.SQLServer.Json",
            _ => "Dashboard.Connection.PostgreSQLServer.Json.JsonPostgreSQLConnection, Syncfusion.Dashboard.Connection.PostgreSQLServer.Json"
        };
    }

    private static string GetPluginUid(string providerKey)
    {
        return providerKey switch
        {
            "sqlserver" => "dc8b01ad-9970-4dab-a066-499ec13a6e21",
            _ => "dc8b01ad-9970-4dab-a066-499ec13a6e22"
        };
    }

    private static string? ReadFirstString(params JsonNode?[] nodes)
    {
        foreach (var node in nodes)
        {
            var value = node?.GetValue<string?>();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string BuildSqlServerDatasourceAddress(JsonObject? connectionProperties, JsonObject datasource)
    {
        var server = ReadFirstString(connectionProperties?["ServerName"], datasource["ServerName"]);
        var port = ReadFirstString(connectionProperties?["Port"], datasource["Port"]);
        return string.IsNullOrWhiteSpace(port) ? server ?? string.Empty : $"{server},{port}";
    }

    private static string BuildSslMode(JsonObject datasource, JsonObject? connectionProperties)
    {
        if (datasource["IsEnableSSL"]?.GetValue<bool?>() == true || connectionProperties?["IsEnableSSL"]?.GetValue<bool?>() == true)
        {
            return "Require";
        }

        return "Prefer";
    }

    private static string SerializeScalarNode(JsonNode? node)
    {
        return node switch
        {
            null => string.Empty,
            JsonValue value when value.TryGetValue<string>(out var stringValue) => stringValue ?? string.Empty,
            _ => node.ToJsonString(new JsonSerializerOptions
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            })
        };
    }

    private static void RemoveNullProperties(JsonObject node)
    {
        var toRemove = new List<string>();
        foreach (var property in node)
        {
            if (property.Value == null)
            {
                toRemove.Add(property.Key);
                continue;
            }

            if (property.Value is JsonObject childObject)
            {
                RemoveNullProperties(childObject);
            }
        }

        foreach (var propertyName in toRemove)
        {
            node.Remove(propertyName);
        }
    }

    private static string SerializeJson(JsonNode node)
    {
        return node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        });
    }

    private static void AddZipEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string BuildDashboardPackageFileName(DashboardPackageData packageData, HarValidationParseBundle parsedBundle, string? sourceFileName)
    {
        var baseName = packageData.DashboardPath
            ?? packageData.DashboardId
            ?? Path.GetFileNameWithoutExtension(sourceFileName ?? "dashboard");
        var sanitized = SanitizeFileNameSegment(baseName);
        return $"{sanitized}-reconstruction.zip";
    }

    private static string BuildDashboardBbixFileName(DashboardPackageData packageData, HarValidationParseBundle parsedBundle, string? sourceFileName)
    {
        var baseName = packageData.DashboardPath
            ?? packageData.DashboardId
            ?? Path.GetFileNameWithoutExtension(sourceFileName ?? "dashboard");
        var sanitized = SanitizeFileNameSegment(baseName);
        return $"{sanitized}-reconstruction.bbix";
    }

    private static string SanitizeFileNameSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) || character == '/' || character == '\\'
                ? '-'
                : character);
        }

        return builder.ToString().Trim('-', ' ');
    }

    private static string BuildBbixContent(DashboardPackageData packageData, DatasourceSchemaReport schemaReport)
    {
        var bbixRoot = new BbixFileEnvelope
        {
            DashboardJson = SerializeJson(packageData.DashboardJson),
            WidgetJson = SerializeJson(packageData.WidgetData),
            FilterJson = SerializeJson(packageData.FilterData),
            ColorSetJson = SerializeJson(packageData.ColorSetData),
            ProgressJson = SerializeBbixInnerJson(BuildBbixProgressJson(schemaReport, packageData)),
            TemplateJson = SerializeBbixInnerJson(BuildBbixTemplateJson(schemaReport, packageData)),
            Resources = null,
            Data = null
        };

        return SerializeBbixEnvelope(bbixRoot);
    }

    private static JsonObject BuildBbixTemplateJson(DatasourceSchemaReport schemaReport, DashboardPackageData packageData)
    {
        var dashboardItemName = ResolveDashboardItemName(packageData);
        var templateObject = new JsonObject
        {
            ["Name"] = dashboardItemName,
            ["FileName"] = $"{dashboardItemName}.SYDJ",
            ["Description"] = packageData.DashboardJson["Description"]?.GetValue<string>()
                ?? packageData.DashboardJson["description"]?.GetValue<string>()
                ?? string.Empty,
            ["Datasources"] = new JsonArray(schemaReport.DatasourceSummaries
                .Select(summary => (JsonNode)new JsonObject
                {
                    ["Id"] = summary.Id,
                    ["Name"] = summary.Name,
                    ["Description"] = string.Empty,
                    ["Type"] = MapDatasourceTypeForTemplate(summary.ProviderType),
                    ["DataSourceId"] = summary.Id
                })
                .ToArray())
        };

        return templateObject;
    }

    private static JsonObject BuildBbixProgressJson(DatasourceSchemaReport schemaReport, DashboardPackageData packageData)
    {
        var progressObject = new JsonObject
        {
            ["Name"] = ResolveDashboardItemName(packageData),
            ["Description"] = packageData.DashboardJson["Description"]?.GetValue<string>()
                ?? packageData.DashboardJson["description"]?.GetValue<string>()
                ?? string.Empty,
            ["CategoryId"] = null,
            ["CategoryName"] = null,
            ["Datasource"] = new JsonArray(schemaReport.DatasourceSummaries
                .Select(summary => (JsonNode)new JsonObject
                {
                    ["Name"] = summary.Name,
                    ["Description"] = string.Empty,
                    ["Type"] = MapDatasourceTypeForTemplate(summary.ProviderType),
                    ["DataSourceId"] = summary.Id,
                    ["OriginalDsId"] = summary.Id,
                    ["CustomConnector"] = null,
                    ["OAuthConnection"] = null,
                    ["Status"] = 2,
                    ["UseExisting"] = false,
                    ["ExistingId"] = null,
                    ["ExistingName"] = null,
                    ["UseMappedDs"] = false,
                    ["MappedDsId"] = null,
                    ["LinkedDatasourceInfo"] = new JsonArray(),
                    ["RefreshSchedule"] = null,
                    ["State"] = 0,
                    ["PublishId"] = null,
                    ["Connector"] = MapDatasourceTypeForTemplate(summary.ProviderType),
                    ["ReplaceId"] = null,
                    ["IsUploaded"] = false
                })
                .ToArray())
        };

        return progressObject;
    }

    private static string ResolveDashboardItemName(DashboardPackageData packageData)
    {
        var preferredName = packageData.DashboardJson["Name"]?.GetValue<string>()
            ?? packageData.DashboardJson["name"]?.GetValue<string>()
            ?? packageData.DashboardPath
            ?? packageData.DashboardId
            ?? "Reconstructed Dashboard";

        return SanitizeDashboardItemName(preferredName);
    }

    private static string SanitizeDashboardItemName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Reconstructed Dashboard";
        }

        const string invalidCharacters = "*|/:<>,%;\"&?#\\";
        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = false;

        foreach (var character in value.Trim())
        {
            if (char.IsControl(character))
            {
                continue;
            }

            if (invalidCharacters.IndexOf(character) >= 0)
            {
                if (!previousWasSeparator)
                {
                    builder.Append(' ');
                    previousWasSeparator = true;
                }

                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!previousWasSeparator)
                {
                    builder.Append(' ');
                    previousWasSeparator = true;
                }

                continue;
            }

            builder.Append(character);
            previousWasSeparator = false;
        }

        var sanitized = builder.ToString().Trim(' ', '.', '-');
        return string.IsNullOrWhiteSpace(sanitized) ? "Reconstructed Dashboard" : sanitized;
    }

    private static string SerializeBbixInnerJson(JsonNode node)
    {
        return node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static string SerializeBbixEnvelope(BbixFileEnvelope envelope)
    {
        return JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            WriteIndented = false,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static string MapDatasourceTypeForTemplate(string? providerType)
    {
        return NormalizeProviderKey(providerType) switch
        {
            "sqlserver" => "SQL",
            "postgresql" => "PostgreSQL",
            _ => "PostgreSQL"
        };
    }

    private static string BuildPackageManifestJson(
        DashboardPackageData packageData,
        HarValidationEntry selectedEntry,
        HarValidationParseBundle parsedBundle,
        string? sourceFileName,
        DatasourceSchemaReport schemaReport,
        int serializationDllCount)
    {
        var manifest = new JsonObject
        {
            ["GeneratedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["SourceHarFile"] = sourceFileName ?? string.Empty,
            ["DashboardPath"] = packageData.DashboardPath ?? parsedBundle.DashboardPath ?? string.Empty,
            ["DashboardId"] = packageData.DashboardId ?? string.Empty,
            ["DashboardObjectId"] = packageData.DashboardObjectId ?? string.Empty,
            ["LoadDashboardRequestKey"] = selectedEntry.Key,
            ["LoadDashboardUrl"] = selectedEntry.Url,
            ["ExtractionMode"] = serializationDllCount > 0
                ? "Fallback JSON parsing with latest serialization DLL folder detected"
                : "Fallback JSON parsing",
            ["SerializationAssemblyCount"] = serializationDllCount,
            ["PackageFiles"] = new JsonArray(
                "dashboard.json",
                "widgetdata.json",
                "filterdata.json",
                "colorset.json",
                "load-dashboard-context.json",
                "datasource-schema.json",
                "readme.txt")
        };

        var databaseFiles = new JsonArray();
        foreach (var script in schemaReport.GeneratedFileNames)
        {
            databaseFiles.Add($"database/{script}");
        }
        manifest["DatabaseFiles"] = databaseFiles;
        manifest["DatasourceCount"] = schemaReport.DatasourceSummaries.Count;

        return SerializeJson(manifest);
    }

    private static string BuildPackageReadme(
        DashboardPackageData packageData,
        HarValidationEntry selectedEntry,
        DatasourceSchemaReport schemaReport,
        int serializationDllCount)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Bold Log Validator - Dashboard HAR Reconstruction Package");
        builder.AppendLine();
        builder.AppendLine($"Generated UTC: {DateTime.UtcNow:O}");
        builder.AppendLine($"LoadDashboard API: {selectedEntry.Url}");
        builder.AppendLine($"Dashboard path: {packageData.DashboardPath ?? "(not found)"}");
        builder.AppendLine();
        builder.AppendLine("Included files");
        builder.AppendLine("- dashboard.json");
        builder.AppendLine("- widgetdata.json");
        builder.AppendLine("- filterdata.json");
        builder.AppendLine("- colorset.json");
        builder.AppendLine("- load-dashboard-context.json");
        builder.AppendLine("- datasource-schema.json");
        builder.AppendLine("- database/*.sql");
        builder.AppendLine();
        builder.AppendLine("How to use");
        builder.AppendLine("1. Create a dummy dashboard with matching datasource provider types.");
        builder.AppendLine("2. Use the datasource-schema.json and database SQL files to create placeholder tables and 10k synthetic rows.");
        builder.AppendLine("3. Replace the generated dashboard resource files in the target dashboard resource folder.");
        builder.AppendLine("4. Update datasource IDs and connection-specific identifiers inside the generated files to match the dummy datasource created in your environment.");
        builder.AppendLine();
        builder.AppendLine("Notes");
        builder.AppendLine("- This package is reconstructed from the LoadDashboard HAR response.");
        builder.AppendLine("- The BBIX export uses a HAR-derived persisted dashboard container model. Validate the generated file in Bold BI before sharing it broadly.");
        builder.AppendLine("- Widget layout, datasource metadata, filter actions, and color settings are preserved as captured in the HAR response.");
        builder.AppendLine("- Synthetic SQL data is inferred from field names and types; it helps recreate structure, not customer business data.");
        builder.AppendLine(serializationDllCount > 0
            ? "- Latest serialization DLLs were detected in the external/serialization folder. The current package still uses fallback JSON splitting to stay version-safe."
            : "- No latest serialization DLLs were detected. The package uses fallback JSON splitting only.");
        builder.AppendLine();
        builder.AppendLine($"Detected datasources: {schemaReport.DatasourceSummaries.Count}");
        foreach (var datasource in schemaReport.DatasourceSummaries)
        {
            builder.AppendLine($"- {datasource.Name} ({datasource.ProviderType}) : {datasource.TableCount} inferred table(s)");
        }

        return builder.ToString();
    }

    private static DatasourceSchemaReport BuildDatasourceSchemaReport(JsonObject dashboardJson, JsonArray? widgetData)
    {
        var datasourcesById = new Dictionary<string, DatasourceSummary>(StringComparer.OrdinalIgnoreCase);
        if (dashboardJson["DataSources"] is JsonArray datasourceArray)
        {
            foreach (var datasourceNode in datasourceArray)
            {
                if (datasourceNode is not JsonObject datasourceObject)
                {
                    continue;
                }

                var datasourceId = datasourceObject["Id"]?.GetValue<string>() ?? datasourceObject["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
                datasourcesById[datasourceId] = new DatasourceSummary
                {
                    Id = datasourceId,
                    Name = datasourceObject["Name"]?.GetValue<string>() ?? datasourceObject["name"]?.GetValue<string>() ?? datasourceId,
                    ProviderType = datasourceObject["ProviderType"]?.GetValue<string>() ?? datasourceObject["providerType"]?.GetValue<string>() ?? "Unknown",
                    ConnectionType = datasourceObject["ConnectionType"]?.GetValue<string>() ?? datasourceObject["connectionType"]?.GetValue<string>()
                };
            }
        }

        var datasets = dashboardJson["DataSets"] as JsonArray;
        if (datasets != null)
        {
            foreach (var datasetNode in datasets)
            {
                if (datasetNode is not JsonObject datasetObject)
                {
                    continue;
                }

                var datasourceId = datasetObject["Id"]?.GetValue<string>() ?? datasetObject["id"]?.GetValue<string>() ?? "unknown-datasource";
                if (!datasourcesById.TryGetValue(datasourceId, out var datasourceSummary))
                {
                    datasourceSummary = new DatasourceSummary
                    {
                        Id = datasourceId,
                        Name = datasetObject["Name"]?.GetValue<string>() ?? datasourceId,
                        ProviderType = "Unknown"
                    };
                    datasourcesById[datasourceId] = datasourceSummary;
                }

                datasourceSummary.DatasetNames.Add(datasetObject["Name"]?.GetValue<string>() ?? datasourceSummary.Name);
                var fields = datasetObject["Fields"] as JsonArray;
                if (fields == null)
                {
                    continue;
                }

                foreach (var fieldNode in fields)
                {
                    if (fieldNode is not JsonObject fieldObject)
                    {
                        continue;
                    }

                    var tableName = fieldObject["TableName"]?.GetValue<string>() ?? datasetObject["Name"]?.GetValue<string>() ?? "UnknownTable";
                    var columnName = fieldObject["DataField"]?.GetValue<string>() ?? fieldObject["Name"]?.GetValue<string>() ?? "UnknownColumn";
                    var typeName = fieldObject["TypeName"]?.GetValue<string>() ?? "String";
                    datasourceSummary.UpsertColumn(tableName, columnName, typeName);
                }

                CollectInitialFilterSeedValues(datasetObject, datasourceSummary, fields);
            }
        }

        foreach (var datasource in datasourcesById.Values)
        {
            datasource.TableCount = datasource.Tables.Count;
        }

        CollectWidgetFilterSeedValues(widgetData, datasourcesById);

        var reportJson = new JsonObject
        {
            ["GeneratedAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["Datasources"] = new JsonArray(datasourcesById.Values.Select(BuildDatasourceJson).ToArray())
        };

        return new DatasourceSchemaReport
        {
            DatasourceSummaries = datasourcesById.Values.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            ReportJson = reportJson
        };
    }

    private static void CollectWidgetFilterSeedValues(JsonArray? widgetData, Dictionary<string, DatasourceSummary> datasourcesById)
    {
        if (widgetData == null)
        {
            return;
        }

        foreach (var widget in widgetData.OfType<JsonObject>())
        {
            if (widget["Data"] is not JsonObject widgetDataNode)
            {
                continue;
            }

            var datasourceSummary = ResolveWidgetDatasource(widgetDataNode, datasourcesById);
            if (datasourceSummary == null)
            {
                continue;
            }

            if (widgetDataNode["Containers"] is not JsonArray containers)
            {
                continue;
            }

            var widgetKey = widget["UniqueName"]?.GetValue<string>()
                ?? widget["UniqueId"]?.GetValue<string>()
                ?? Guid.NewGuid().ToString("N");
            var mergedWidgetSeedRows = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var container in containers.OfType<JsonObject>())
            {
                CollectWidgetContainerFieldSeeds(container["FieldInfos"] as JsonArray, datasourceSummary, widgetKey, mergedWidgetSeedRows);
                CollectWidgetContainerFieldSeeds(container["Values"] as JsonArray, datasourceSummary, widgetKey, mergedWidgetSeedRows);
            }

            foreach (var mergedRow in mergedWidgetSeedRows)
            {
                datasourceSummary.AddSeedRow(mergedRow.Key, $"{widgetKey}:merged", mergedRow.Value);
            }
        }
    }

    private static DatasourceSummary? ResolveWidgetDatasource(JsonObject widgetDataNode, Dictionary<string, DatasourceSummary> datasourcesById)
    {
        var datasourceId = widgetDataNode["Id"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(datasourceId) && datasourcesById.TryGetValue(datasourceId, out var byId))
        {
            return byId;
        }

        var datasetName = widgetDataNode["DataSetName"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(datasetName))
        {
            return datasourcesById.Values.FirstOrDefault(summary => summary.DatasetNames.Contains(datasetName));
        }

        return null;
    }

    private static void CollectWidgetContainerFieldSeeds(JsonArray? fieldInfos, DatasourceSummary datasourceSummary, string widgetKey, Dictionary<string, Dictionary<string, string>> mergedWidgetSeedRows)
    {
        if (fieldInfos == null)
        {
            return;
        }

        foreach (var fieldInfo in fieldInfos.OfType<JsonObject>())
        {
            var filterInfo = fieldInfo["FilterInfo"] as JsonObject;
            if (filterInfo == null)
            {
                continue;
            }

            var fieldName = fieldInfo["Name"]?.GetValue<string>()
                ?? fieldInfo["DisplayName"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                continue;
            }

            foreach (var seedValue in ExtractWidgetFilterValues(filterInfo))
            {
                UpsertWidgetSeedValue(datasourceSummary, fieldName, seedValue, widgetKey, mergedWidgetSeedRows);
            }
        }
    }

    private static IEnumerable<string> ExtractWidgetFilterValues(JsonObject filterInfo)
    {
        if (filterInfo["DimensionFilterInfo"] is JsonObject dimensionFilterInfo)
        {
            foreach (var filterValue in ReadFilterValues(dimensionFilterInfo["AllowFilterInfo"]?["FilterValues"] as JsonArray))
            {
                yield return filterValue;
            }
        }

        if (filterInfo["MeasureFilterInfo"] is JsonObject measureFilterInfo)
        {
            foreach (var filterValue in ReadFilterValues(measureFilterInfo["FilterValues"] as JsonArray))
            {
                yield return filterValue;
            }
        }

        if (filterInfo["RelativeDateFilterInfo"] is JsonObject relativeDateFilterInfo)
        {
            var seedValue = relativeDateFilterInfo["SelectedRangeforRelativeFilter"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(seedValue))
            {
                yield return seedValue;
            }
        }
    }

    private static void UpsertWidgetSeedValue(DatasourceSummary datasourceSummary, string columnName, string value, string widgetKey, Dictionary<string, Dictionary<string, string>> mergedWidgetSeedRows)
    {
        var matchingTables = datasourceSummary.Tables
            .Where(table => table.Value.Columns.Any(column => string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matchingTables.Count == 0)
        {
            return;
        }

        var targetTable = matchingTables.Count == 1
            ? matchingTables[0].Key
            : matchingTables
                .OrderByDescending(table => table.Value.ContainsColumnInAnySeedRow(columnName))
                .ThenBy(table => table.Key, StringComparer.OrdinalIgnoreCase)
                .First().Key;

        if (!mergedWidgetSeedRows.TryGetValue(targetTable, out var mergedRow))
        {
            mergedRow = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            mergedWidgetSeedRows[targetTable] = mergedRow;
        }

        mergedRow[columnName] = value;
        datasourceSummary.UpsertFilterSeedValue(targetTable, columnName, value, $"{widgetKey}:{columnName}:{value}");
    }

    private static IEnumerable<string> ReadFilterValues(JsonArray? values)
    {
        if (values == null)
        {
            yield break;
        }

        foreach (var value in values.OfType<JsonNode>())
        {
            var stringValue = value.GetValue<string?>();
            if (!string.IsNullOrWhiteSpace(stringValue))
            {
                yield return stringValue;
            }
        }
    }

    private static void NormalizePortableDatasourceProviders(DashboardPackageData packageData)
    {
        if (packageData.SourceDashboardJson["DataSources"] is JsonArray sourceDatasourceArray)
        {
            foreach (var datasourceNode in sourceDatasourceArray.OfType<JsonObject>())
            {
                NormalizePortableDatasourceProvider(datasourceNode);
            }
        }

        if (packageData.DashboardJson["connections"] is JsonArray connectionArray)
        {
            foreach (var connectionNode in connectionArray.OfType<JsonObject>())
            {
                NormalizePortableConnectionProvider(connectionNode);
            }
        }

        if (packageData.DashboardJson["datasources"] is JsonArray persistedDatasourceArray)
        {
            foreach (var datasourceNode in persistedDatasourceArray.OfType<JsonObject>())
            {
                NormalizePortablePersistedDatasource(datasourceNode);
            }
        }

        if (packageData.ContextJson["Datasources"] is JsonArray contextDatasources)
        {
            foreach (var datasourceNode in contextDatasources.OfType<JsonObject>())
            {
                var providerType = datasourceNode["Type"]?.GetValue<string>()
                    ?? datasourceNode["DataSourceType"]?.GetValue<string>()
                    ?? datasourceNode["ProviderType"]?.GetValue<string>();
                if (!ShouldNormalizeToPortablePostgres(providerType))
                {
                    continue;
                }

                datasourceNode["Type"] = "PostgreSQL";
                datasourceNode["DataSourceType"] = "PostgreSQL";
                datasourceNode["ProviderType"] = "PostgreSQL";
            }
        }
    }

    private static void NormalizePortableDatasourceProvider(JsonObject datasourceObject)
    {
        var providerType = datasourceObject["ProviderType"]?.GetValue<string>()
            ?? datasourceObject["providerType"]?.GetValue<string>()
            ?? datasourceObject["Type"]?.GetValue<string>()
            ?? datasourceObject["type"]?.GetValue<string>()
            ?? datasourceObject["Connector"]?.GetValue<string>()
            ?? datasourceObject["connector"]?.GetValue<string>();

        if (!ShouldNormalizeToPortablePostgres(providerType))
        {
            return;
        }

        datasourceObject["ProviderType"] = "PostgreSQL";
        datasourceObject["providerType"] = "PostgreSQL";
        datasourceObject["ConnectionType"] = "PostgreSQL";
        datasourceObject["connectionType"] = "PostgreSQL";
        datasourceObject["Type"] = "PostgreSQL";
        datasourceObject["type"] = "PostgreSQL";
        datasourceObject["Connector"] = "PostgreSQL";
        datasourceObject["connector"] = "PostgreSQL";
        datasourceObject["$type"] = "Dashboard.Connection.PostgreSQLServer.Json.JsonPostgreSQLConnection, Syncfusion.Dashboard.Connection.PostgreSQLServer.Json";
    }

    private static void NormalizePortableConnectionProvider(JsonObject connectionObject)
    {
        var providerType = connectionObject["$type"]?.GetValue<string>()
            ?? connectionObject["connectiontype"]?.GetValue<string>()
            ?? connectionObject["name"]?.GetValue<string>();

        if (!ShouldNormalizeToPortablePostgres(providerType))
        {
            return;
        }

        connectionObject["$type"] = "Dashboard.Connection.PostgreSQLServer.Json.JsonPostgreSQLConnection, Syncfusion.Dashboard.Connection.PostgreSQLServer.Json";
        if (connectionObject["datasource"] != null)
        {
            connectionObject["serverName"] = connectionObject["datasource"]!.DeepClone();
            connectionObject.Remove("datasource");
        }

        if (connectionObject["initialCatalog"] != null)
        {
            connectionObject["database"] = connectionObject["initialCatalog"]!.DeepClone();
            connectionObject.Remove("initialCatalog");
        }

        if (connectionObject["username"] != null)
        {
            connectionObject["userName"] = connectionObject["username"]!.DeepClone();
            connectionObject.Remove("username");
        }

        if (connectionObject["password"] == null)
        {
            connectionObject["password"] = string.Empty;
        }

        connectionObject["portNumber"] = connectionObject["portNumber"] ?? "5432";
        connectionObject["sslMode"] = connectionObject["sslMode"] ?? "Prefer";
        connectionObject["connectiontype"] = "PostgreSQL";
        connectionObject["pluginUID"] = GetPluginUid("postgresql");
    }

    private static void NormalizePortablePersistedDatasource(JsonObject datasourceObject)
    {
        if (datasourceObject["tables"] is not JsonArray tables)
        {
            return;
        }

        foreach (var table in tables.OfType<JsonObject>())
        {
            table["schema"] = table["schema"] ?? "public";
        }
    }

    private static bool ShouldNormalizeToPortablePostgres(string? providerType)
    {
        var providerKey = NormalizeProviderKey(providerType);
        return !string.Equals(providerKey, "postgresql", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(providerKey, "sqlserver", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode BuildDatasourceJson(DatasourceSummary datasource)
    {
        var tables = new JsonArray();
            foreach (var table in datasource.Tables.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                var columns = new JsonArray();
            foreach (var column in table.Value.Columns.OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                columns.Add(new JsonObject
                {
                    ["Name"] = column.Name,
                    ["TypeName"] = column.TypeName
                });
            }

                var tableJson = new JsonObject
            {
                ["Name"] = table.Key,
                ["Columns"] = columns
            };

                if (table.Value.FilterSeedRows.Count > 0)
                {
                    tableJson["FilterSeedRow"] = new JsonObject(table.Value.FilterSeedRows[0]
                        .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(static item => new KeyValuePair<string, JsonNode?>(item.Key, JsonValue.Create(item.Value))));

                    tableJson["FilterSeedRows"] = new JsonArray(table.Value.FilterSeedRows
                        .Select(row => (JsonNode)new JsonObject(row
                            .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                            .Select(static item => new KeyValuePair<string, JsonNode?>(item.Key, JsonValue.Create(item.Value)))))
                        .ToArray());
                }

                tables.Add(tableJson);
        }

        return new JsonObject
        {
            ["Id"] = datasource.Id,
            ["Name"] = datasource.Name,
            ["ProviderType"] = datasource.ProviderType,
            ["ConnectionType"] = datasource.ConnectionType,
            ["DatasetNames"] = new JsonArray(datasource.DatasetNames
                .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
                .Select(static item => JsonValue.Create(item))
                .ToArray()),
            ["Tables"] = tables
        };
    }

    private static List<GeneratedSqlFile> BuildDatasourceBootstrapScripts(DatasourceSchemaReport schemaReport)
    {
        var scripts = new List<GeneratedSqlFile>();
        foreach (var providerGroup in schemaReport.DatasourceSummaries.GroupBy(static item => NormalizeProviderKey(item.ProviderType), StringComparer.OrdinalIgnoreCase))
        {
            var providerKey = providerGroup.Key;
            var providerItems = providerGroup.ToList();
            var content = providerKey switch
            {
                "postgresql" => BuildPostgreSqlBootstrapScript(providerItems),
                "sqlserver" => BuildSqlServerBootstrapScript(providerItems),
                _ => BuildPostgreSqlBootstrapScript(providerItems, $"Provider '{providerItems[0].ProviderType}' is not mapped directly, so this file uses PostgreSQL-compatible schema and seed SQL as a portable fallback.")
            };

            var fileName = providerKey switch
            {
                "postgresql" => "postgresql-bootstrap.sql",
                "sqlserver" => "sqlserver-bootstrap.sql",
                _ => $"{providerKey}-postgresql-bootstrap.sql"
            };

            scripts.Add(new GeneratedSqlFile
            {
                FileName = fileName,
                Content = content
            });
            schemaReport.GeneratedFileNames.Add(fileName);
        }

        return scripts;
    }

    private static string NormalizeProviderKey(string? providerType)
    {
        var normalized = (providerType ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Contains("postgre"))
        {
            return "postgresql";
        }

        if (normalized == "sql" || normalized.Contains("sql server") || normalized.Contains("mssql") || normalized == "sqlserver")
        {
            return "sqlserver";
        }

        return string.IsNullOrWhiteSpace(normalized) ? "generic" : Regex.Replace(normalized, "[^a-z0-9]+", "-");
    }

    private static string BuildPostgreSqlBootstrapScript(List<DatasourceSummary> datasources, string? prefaceNote = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("-- Generated from LoadDashboard HAR response");
        builder.AppendLine("-- Creates inferred tables and inserts 10,000 synthetic rows for each table.");
        if (!string.IsNullOrWhiteSpace(prefaceNote))
        {
            builder.AppendLine($"-- {prefaceNote}");
        }
        builder.AppendLine();

        foreach (var datasource in datasources)
        {
            builder.AppendLine($"-- Datasource: {datasource.Name} ({datasource.ProviderType})");
            foreach (var table in datasource.Tables.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                var physicalTableName = QuotePostgresIdentifier(table.Key);
                builder.AppendLine($"CREATE TABLE IF NOT EXISTS {physicalTableName} (");
                builder.AppendLine(string.Join("," + Environment.NewLine, table.Value.Columns.Select(BuildPostgresColumnDefinition)));
                builder.AppendLine(");");
                builder.AppendLine();

                var columnNames = string.Join(", ", table.Value.Columns.Select(column => QuotePostgresIdentifier(column.Name)));
                if (table.Value.FilterSeedRows.Count > 0)
                {
                    for (var seedRowIndex = 0; seedRowIndex < table.Value.FilterSeedRows.Count; seedRowIndex++)
                    {
                        var filterSeedExpressions = string.Join(", ", table.Value.Columns.Select(column => BuildPostgresFilterSeedExpression(column, table.Value.FilterSeedRows[seedRowIndex], 10001 + seedRowIndex)));
                        builder.AppendLine($"INSERT INTO {physicalTableName} ({columnNames})");
                        builder.AppendLine($"VALUES ({filterSeedExpressions});");
                        builder.AppendLine();
                    }
                }

                var valueExpressions = string.Join(", ", table.Value.Columns.Select(static column => BuildPostgresSeedExpression(column)));
                builder.AppendLine($"INSERT INTO {physicalTableName} ({columnNames})");
                builder.AppendLine($"SELECT {valueExpressions}");
                builder.AppendLine("FROM generate_series(1, 10000) AS gs(n);");
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string BuildSqlServerBootstrapScript(List<DatasourceSummary> datasources)
    {
        var builder = new StringBuilder();
        builder.AppendLine("-- Generated from LoadDashboard HAR response");
        builder.AppendLine("-- Creates inferred tables and inserts 10,000 synthetic rows for each table.");
        builder.AppendLine();

        foreach (var datasource in datasources)
        {
            builder.AppendLine($"-- Datasource: {datasource.Name} ({datasource.ProviderType})");
            foreach (var table in datasource.Tables.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                var physicalTableName = QuoteSqlServerTableIdentifier(table.Key);
                builder.AppendLine($"IF OBJECT_ID(N'dbo.{EscapeSqlLiteral(table.Key)}', N'U') IS NULL");
                builder.AppendLine("BEGIN");
                builder.AppendLine($"    CREATE TABLE {physicalTableName} (");
                builder.AppendLine(string.Join("," + Environment.NewLine, table.Value.Columns.Select(static column => "        " + BuildSqlServerColumnDefinition(column))));
                builder.AppendLine("    );");
                builder.AppendLine("END;");
                builder.AppendLine();

                var columnNames = string.Join(", ", table.Value.Columns.Select(column => QuoteSqlServerColumnIdentifier(column.Name)));
                if (table.Value.FilterSeedRows.Count > 0)
                {
                    for (var seedRowIndex = 0; seedRowIndex < table.Value.FilterSeedRows.Count; seedRowIndex++)
                    {
                        var filterSeedExpressions = string.Join(", ", table.Value.Columns.Select(column => BuildSqlServerFilterSeedExpression(column, table.Value.FilterSeedRows[seedRowIndex], 10001 + seedRowIndex)));
                        builder.AppendLine($"INSERT INTO {physicalTableName} ({columnNames})");
                        builder.AppendLine($"VALUES ({filterSeedExpressions});");
                        builder.AppendLine();
                    }
                }

                var valueExpressions = string.Join(", ", table.Value.Columns.Select(static column => BuildSqlServerSeedExpression(column)));
                builder.AppendLine(";WITH nums AS (");
                builder.AppendLine("    SELECT TOP (10000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n");
                builder.AppendLine("    FROM sys.all_objects a CROSS JOIN sys.all_objects b");
                builder.AppendLine(")");
                builder.AppendLine($"INSERT INTO {physicalTableName} ({columnNames})");
                builder.AppendLine($"SELECT {valueExpressions}");
                builder.AppendLine("FROM nums;");
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string BuildPostgresColumnDefinition(InferredColumn column)
    {
        return $"    {QuotePostgresIdentifier(column.Name)} {MapPostgresType(column.TypeName)}";
    }

    private static string BuildSqlServerColumnDefinition(InferredColumn column)
    {
        return $"{QuoteSqlServerColumnIdentifier(column.Name)} {MapSqlServerType(column.TypeName)}";
    }

    private static string BuildPostgresSeedExpression(InferredColumn column)
    {
        return NormalizeTypeName(column.TypeName) switch
        {
            "boolean" => "(gs.n % 2 = 0)",
            "integer" => "gs.n",
            "real" => "ROUND((gs.n * 1.1)::numeric, 2)",
            "date" => "(DATE '2024-01-01' + ((gs.n - 1) % 365))",
            "datetime" => "(TIMESTAMP '2024-01-01 00:00:00' + (((gs.n - 1) % 10000) * INTERVAL '1 minute'))",
            _ => $"'sample_{EscapeSqlLiteral(column.Name)}_' || gs.n"
        };
    }

    private static string BuildSqlServerSeedExpression(InferredColumn column)
    {
        return NormalizeTypeName(column.TypeName) switch
        {
            "boolean" => "CASE WHEN nums.n % 2 = 0 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END",
            "integer" => "nums.n",
            "real" => "CAST(nums.n * 1.1 AS decimal(18,2))",
            "date" => "DATEADD(day, (nums.n - 1) % 365, CAST('2024-01-01' AS date))",
            "datetime" => "DATEADD(minute, (nums.n - 1) % 10000, CAST('2024-01-01T00:00:00' AS datetime2))",
            _ => $"CONCAT('sample_{EscapeSqlLiteral(column.Name)}_', nums.n)"
        };
    }

    private static void CollectInitialFilterSeedValues(JsonObject datasetObject, DatasourceSummary datasourceSummary, JsonArray fields)
    {
        if (datasetObject["InitialFilterInfo"] is not JsonArray initialFilters)
        {
            return;
        }

        var mergedRows = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var rowIndex = 0;
        foreach (var filterObject in initialFilters.OfType<JsonObject>())
        {
            var candidate = TryBuildInitialFilterSeedValue(filterObject, fields);
            if (candidate == null)
            {
                continue;
            }

            datasourceSummary.UpsertFilterSeedValue(candidate.Value.TableName, candidate.Value.ColumnName, candidate.Value.Value, $"initial:{rowIndex}:{candidate.Value.ColumnName}:{candidate.Value.Value}");
            if (!mergedRows.TryGetValue(candidate.Value.TableName, out var mergedRow))
            {
                mergedRow = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                mergedRows[candidate.Value.TableName] = mergedRow;
            }

            mergedRow[candidate.Value.ColumnName] = candidate.Value.Value;
            rowIndex++;
        }

        foreach (var mergedRow in mergedRows)
        {
            datasourceSummary.AddSeedRow(mergedRow.Key, "initial:merged", mergedRow.Value);
        }
    }

    private static (string TableName, string ColumnName, string Value)? TryBuildInitialFilterSeedValue(JsonObject filterObject, JsonArray fields)
    {
        var tableName = filterObject["TableName"]?.GetValue<string>();
        string? columnName = null;
        string? value = null;

        if (filterObject["DimensionFilterSchemaInfo"] is JsonObject dimensionInfo)
        {
            columnName = dimensionInfo["ColumnName"]?.GetValue<string>();
            value = ReadFirstFilterValue(dimensionInfo["FilterValues"] as JsonArray);
        }
        else if (filterObject["DateFilterSchemaInfo"] is JsonObject dateInfo)
        {
            columnName = dateInfo["ColumnName"]?.GetValue<string>();
            value = ReadFirstFilterValue(dateInfo["FilterValues"] as JsonArray)
                ?? dateInfo["DateFormatInfo"]?["StartDate"]?.GetValue<string>()
                ?? dateInfo["DateFormatInfo"]?["EndDate"]?.GetValue<string>();
        }
        else if (filterObject["MeasureFilterSchemaInfo"] is JsonObject measureInfo)
        {
            columnName = measureInfo["ColumnName"]?.GetValue<string>();
            value = ReadFirstFilterValue(measureInfo["FilterValues"] as JsonArray);
        }
        else if (filterObject["BooleanFilterSchemaInfo"] is JsonObject booleanInfo)
        {
            columnName = booleanInfo["ColumnName"]?.GetValue<string>();
            if (booleanInfo["IsTrueEnabled"]?.GetValue<bool?>() == true)
            {
                value = "true";
            }
            else if (booleanInfo["IsFalseEnabled"]?.GetValue<bool?>() == true)
            {
                value = "false";
            }
        }

        if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(tableName))
        {
            tableName = fields
                .OfType<JsonObject>()
                .FirstOrDefault(field =>
                    string.Equals(field["DataField"]?.GetValue<string>(), columnName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(field["Name"]?.GetValue<string>(), columnName, StringComparison.OrdinalIgnoreCase))
                ?["TableName"]?.GetValue<string>();
        }

        return string.IsNullOrWhiteSpace(tableName)
            ? null
            : (tableName, columnName, value);
    }

    private static string? ReadFirstFilterValue(JsonArray? values)
    {
        return values?
            .OfType<JsonNode>()
            .Select(static item => item.GetValue<string?>())
            .FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item));
    }

    private static string BuildPostgresFilterSeedExpression(InferredColumn column, IReadOnlyDictionary<string, string> seedValues, int seedOrdinal)
    {
        return seedValues.TryGetValue(column.Name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? FormatPostgresLiteral(column.TypeName, value)
            : BuildPostgresSeedExpression(column).Replace("gs.n", seedOrdinal.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string BuildSqlServerFilterSeedExpression(InferredColumn column, IReadOnlyDictionary<string, string> seedValues, int seedOrdinal)
    {
        return seedValues.TryGetValue(column.Name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? FormatSqlServerLiteral(column.TypeName, value)
            : BuildSqlServerSeedExpression(column).Replace("nums.n", seedOrdinal.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string FormatPostgresLiteral(string typeName, string value)
    {
        return NormalizeTypeName(typeName) switch
        {
            "boolean" => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ? "TRUE" : "FALSE",
            "integer" => int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var intValue) ? intValue.ToString(CultureInfo.InvariantCulture) : "10001",
            "real" => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var decimalValue) ? decimalValue.ToString(CultureInfo.InvariantCulture) : "10001.00",
            "date" => $"DATE '{EscapeSqlLiteral(value)}'",
            "datetime" => $"TIMESTAMP '{EscapeSqlLiteral(value)}'",
            _ => $"'{EscapeSqlLiteral(value)}'"
        };
    }

    private static string FormatSqlServerLiteral(string typeName, string value)
    {
        return NormalizeTypeName(typeName) switch
        {
            "boolean" => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ? "CAST(1 AS bit)" : "CAST(0 AS bit)",
            "integer" => int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var intValue) ? intValue.ToString(CultureInfo.InvariantCulture) : "10001",
            "real" => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var decimalValue) ? decimalValue.ToString(CultureInfo.InvariantCulture) : "10001.00",
            "date" => $"CAST('{EscapeSqlLiteral(value)}' AS date)",
            "datetime" => $"CAST('{EscapeSqlLiteral(value)}' AS datetime2)",
            _ => $"N'{EscapeSqlLiteral(value)}'"
        };
    }

    private static string QuotePostgresIdentifier(string value)
    {
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string QuoteSqlServerTableIdentifier(string value)
    {
        return $"[dbo].[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    private static string QuoteSqlServerColumnIdentifier(string value)
    {
        return $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    private static string EscapeSqlLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string MapPostgresType(string typeName)
    {
        return NormalizeTypeName(typeName) switch
        {
            "boolean" => "boolean",
            "integer" => "integer",
            "real" => "numeric(18,2)",
            "date" => "date",
            "datetime" => "timestamp",
            _ => "text"
        };
    }

    private static string MapSqlServerType(string typeName)
    {
        return NormalizeTypeName(typeName) switch
        {
            "boolean" => "bit",
            "integer" => "int",
            "real" => "decimal(18,2)",
            "date" => "date",
            "datetime" => "datetime2",
            _ => "nvarchar(255)"
        };
    }

    private static string NormalizeTypeName(string? typeName)
    {
        var normalized = (typeName ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Contains("bool"))
        {
            return "boolean";
        }

        if (normalized.Contains("int"))
        {
            return "integer";
        }

        if (normalized.Contains("real") || normalized.Contains("double") || normalized.Contains("float") || normalized.Contains("decimal") || normalized.Contains("numeric"))
        {
            return "real";
        }

        if (normalized.Contains("timestamp") || normalized.Contains("datetime"))
        {
            return "datetime";
        }

        if (normalized.Contains("date"))
        {
            return "date";
        }

        return "string";
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

        return CollectRecognizedLogFiles(candidateRoot);
    }

    private static List<string> CollectRecognizedLogFiles(string rootPath)
    {
        return Directory
            .EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Where(static path =>
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
            ? CollectRecognizedLogFiles(_activeUploadLogRoot)
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

    private bool HasSavedUploadedLogs()
    {
        return Directory.Exists(_activeUploadLogRoot)
            && CollectRecognizedLogFiles(_activeUploadLogRoot).Count > 0;
    }

    private void CacheTimelineEntries(string analysisSessionId, List<ParsedLogEntry> entries)
    {
        _memoryCache.Set(
            GetTimelineCacheKey(analysisSessionId),
            entries,
            new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(TimelineCacheMinutes),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(TimelineCacheMinutes)
            });
    }

    private static string GetTimelineCacheKey(string analysisSessionId)
    {
        return $"timeline:{analysisSessionId}";
    }

    private void CacheRepeatedLogEntries(string analysisSessionId, List<GroupedLogSummary> entries)
    {
        _memoryCache.Set(
            GetRepeatedLogCacheKey(analysisSessionId),
            entries,
            new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(TimelineCacheMinutes),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(TimelineCacheMinutes)
            });
    }

    private static string GetRepeatedLogCacheKey(string analysisSessionId)
    {
        return $"repeated:{analysisSessionId}";
    }

    private void CacheHarApiEntries(string analysisSessionId, List<HarValidationApiItem> entries)
    {
        _memoryCache.Set(
            GetHarApiCacheKey(analysisSessionId),
            entries,
            new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(TimelineCacheMinutes),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(TimelineCacheMinutes)
            });
    }

    private static string GetHarApiCacheKey(string analysisSessionId)
    {
        return $"harapis:{analysisSessionId}";
    }

    private static TimelinePageResponse BuildTimelinePageResponse(
        List<ParsedLogEntry> entries,
        string? timelineService,
        string? timelineSortOrder,
        int skip)
    {
        var effectiveTimelineService = string.IsNullOrWhiteSpace(timelineService)
            || string.Equals(timelineService, "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : timelineService;

        var filteredTimelineEntries = entries
            .Where(entry => string.IsNullOrWhiteSpace(effectiveTimelineService)
                || entry.Service.Equals(effectiveTimelineService, StringComparison.OrdinalIgnoreCase));

        var orderedTimelineEntries = (string.Equals(timelineSortOrder, "asc", StringComparison.OrdinalIgnoreCase)
                ? filteredTimelineEntries.OrderBy(static entry => entry.Timestamp)
                : filteredTimelineEntries.OrderByDescending(static entry => entry.Timestamp))
            .ThenBy(static entry => entry.Service)
            .ThenBy(static entry => entry.LineNumber)
            .ToList();

        var safeSkip = Math.Max(skip, 0);
        var pageEntries = orderedTimelineEntries
            .Skip(safeSkip)
            .Take(TimelinePageSize)
            .ToList();

        return new TimelinePageResponse
        {
            Entries = pageEntries,
            ReturnedCount = pageEntries.Count,
            TotalCount = orderedTimelineEntries.Count,
            HasMore = safeSkip + pageEntries.Count < orderedTimelineEntries.Count
        };
    }

    private static RepeatedLogPageResponse BuildRepeatedLogPageResponse(
        List<GroupedLogSummary> entries,
        string? service,
        int skip)
    {
        var effectiveService = string.IsNullOrWhiteSpace(service)
            || string.Equals(service, "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : service;

        var filteredEntries = entries
            .Where(entry => string.IsNullOrWhiteSpace(effectiveService)
                || entry.Service.Equals(effectiveService, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var safeSkip = Math.Max(skip, 0);
        var pageEntries = filteredEntries
            .Skip(safeSkip)
            .Take(RepeatedLogPageSize)
            .ToList();

        return new RepeatedLogPageResponse
        {
            Entries = pageEntries,
            ReturnedCount = pageEntries.Count,
            TotalCount = filteredEntries.Count,
            HasMore = safeSkip + pageEntries.Count < filteredEntries.Count
        };
    }

    private static HarApiPageResponse BuildHarApiPageResponse(List<HarValidationApiItem> entries, int skip)
    {
        var safeSkip = Math.Max(skip, 0);
        var pageEntries = entries
            .Skip(safeSkip)
            .Take(HarApiPageSize)
            .ToList();

        return new HarApiPageResponse
        {
            Entries = pageEntries,
            ReturnedCount = pageEntries.Count,
            TotalCount = entries.Count,
            HasMore = safeSkip + pageEntries.Count < entries.Count
        };
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
        return CollectRecognizedLogFiles(rootPath);
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

    private sealed class DashboardPackageData
    {
        public JsonObject DashboardJson { get; set; } = new();

        public JsonObject SourceDashboardJson { get; set; } = new();

        public JsonArray WidgetData { get; set; } = [];

        public JsonNode FilterData { get; set; } = new JsonObject();

        public JsonArray ColorSetData { get; set; } = [];

        public JsonObject ContextJson { get; set; } = new();

        public string? DashboardPath { get; set; }

        public string? DashboardId { get; set; }

        public string? DashboardObjectId { get; set; }
    }

    private sealed class DatasourceSchemaReport
    {
        public List<DatasourceSummary> DatasourceSummaries { get; set; } = [];

        public JsonObject ReportJson { get; set; } = new();

        public List<string> GeneratedFileNames { get; set; } = [];
    }

    private sealed class DatasourceSummary
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string ProviderType { get; set; } = "Unknown";

        public string? ConnectionType { get; set; }

        public int TableCount { get; set; }

        public HashSet<string> DatasetNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, InferredTable> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void UpsertColumn(string tableName, string columnName, string typeName)
        {
            if (!Tables.TryGetValue(tableName, out var table))
            {
                table = new InferredTable();
                Tables[tableName] = table;
            }

            if (table.Columns.Any(column => string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            table.Columns.Add(new InferredColumn
            {
                Name = columnName,
                TypeName = typeName
            });
        }

        public void UpsertFilterSeedValue(string tableName, string columnName, string value, string rowKey)
        {
            if (!Tables.TryGetValue(tableName, out var table))
            {
                table = new InferredTable();
                Tables[tableName] = table;
            }

            table.UpsertSeedValue(rowKey, columnName, value);
        }

        public void AddSeedRow(string tableName, string rowKey, IReadOnlyDictionary<string, string> values)
        {
            if (!Tables.TryGetValue(tableName, out var table))
            {
                table = new InferredTable();
                Tables[tableName] = table;
            }

            table.AddSeedRow(rowKey, values);
        }
    }

    private sealed class InferredTable
    {
        public List<InferredColumn> Columns { get; } = [];

        private Dictionary<string, Dictionary<string, string>> SeedRowsByKey { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<Dictionary<string, string>> FilterSeedRows => SeedRowsByKey.Values
            .Where(static row => row.Count > 0)
            .Select(static row => row)
            .ToList();

        public void UpsertSeedValue(string rowKey, string columnName, string value)
        {
            if (!SeedRowsByKey.TryGetValue(rowKey, out var row))
            {
                row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                SeedRowsByKey[rowKey] = row;
            }

            row[columnName] = value;
        }

        public void AddSeedRow(string rowKey, IReadOnlyDictionary<string, string> values)
        {
            if (!SeedRowsByKey.TryGetValue(rowKey, out var row))
            {
                row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                SeedRowsByKey[rowKey] = row;
            }

            foreach (var value in values)
            {
                row[value.Key] = value.Value;
            }
        }

        public bool ContainsColumnInAnySeedRow(string columnName)
        {
            return SeedRowsByKey.Values.Any(row => row.ContainsKey(columnName));
        }
    }

    private sealed class InferredColumn
    {
        public string Name { get; set; } = string.Empty;

        public string TypeName { get; set; } = "String";
    }

    private sealed class GeneratedSqlFile
    {
        public string FileName { get; set; } = string.Empty;

        public string Content { get; set; } = string.Empty;
    }

    private sealed class BbixFileEnvelope
    {
        public string DashboardJson { get; set; } = string.Empty;

        public string WidgetJson { get; set; } = string.Empty;

        public string FilterJson { get; set; } = string.Empty;

        public string ColorSetJson { get; set; } = string.Empty;

        public string ProgressJson { get; set; } = string.Empty;

        public string TemplateJson { get; set; } = string.Empty;

        public List<object>? Resources { get; set; }

        public List<object>? Data { get; set; }
    }

    private sealed record IntegrationLocation(string DisplayPath, int FileCount, bool Exists);
}
