using BoldLogValidator.Models;

namespace BoldLogValidator.Services;

public interface ILogAnalysisService
{
    Task<AnalysisResult> AnalyzeAsync(AnalysisFilterInput filter, CancellationToken cancellationToken = default);

    Task<TimelinePageResponse> GetTimelineEntriesAsync(TimelinePageRequest request, CancellationToken cancellationToken = default);

    Task<RepeatedLogPageResponse> GetRepeatedLogEntriesAsync(RepeatedLogPageRequest request, CancellationToken cancellationToken = default);

    Task<RawLogViewModel> GetRawLogViewAsync(RawLogViewFilter filter, CancellationToken cancellationToken = default);

    Task<HarValidationResult> GetHarValidationAsync(HarValidationFilterInput filter, CancellationToken cancellationToken = default);

    Task<HarApiPageResponse> GetHarApiEntriesAsync(HarApiPageRequest request, CancellationToken cancellationToken = default);

    Task<HarRequestDetailsResult> GetHarRequestDetailsAsync(string? requestKey, CancellationToken cancellationToken = default);

    Task<HarDashboardPackageExport> GenerateHarDashboardPackageAsync(
        HarValidationFilterInput filter,
        string? requestKey,
        HarDashboardExportFormat exportFormat = HarDashboardExportFormat.Zip,
        CancellationToken cancellationToken = default);
}
