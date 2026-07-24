using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using BoldLogValidator.Models;
using BoldLogValidator.Services;

namespace BoldLogValidator.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;
    private readonly ILogAnalysisService _logAnalysisService;

    public HomeController(ILogger<HomeController> logger, ILogAnalysisService logAnalysisService)
    {
        _logger = logger;
        _logAnalysisService = logAnalysisService;
    }

    public IActionResult Index()
    {
        return View(new HomePageViewModel());
    }

    [HttpGet]
    public IActionResult HarValidation()
    {
        return View(new HarValidationPageViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestFormLimits(MultipartBodyLengthLimit = 268_435_456)]
    public async Task<IActionResult> Analyze(HomePageViewModel model, CancellationToken cancellationToken)
    {
        model.Result = await _logAnalysisService.AnalyzeAsync(model.Filter, cancellationToken);
        return View("Index", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TimelineEntries([FromForm] TimelinePageRequest request, CancellationToken cancellationToken)
    {
        var result = await _logAnalysisService.GetTimelineEntriesAsync(request, cancellationToken);
        return Json(result);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RepeatedLogEntries([FromForm] RepeatedLogPageRequest request, CancellationToken cancellationToken)
    {
        var result = await _logAnalysisService.GetRepeatedLogEntriesAsync(request, cancellationToken);
        return Json(result);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestFormLimits(MultipartBodyLengthLimit = 268_435_456)]
    public async Task<IActionResult> HarValidation(HarValidationPageViewModel model, CancellationToken cancellationToken)
    {
        model.Result = await _logAnalysisService.GetHarValidationAsync(model.Filter, cancellationToken);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> HarApiEntries([FromForm] HarApiPageRequest request, CancellationToken cancellationToken)
    {
        var result = await _logAnalysisService.GetHarApiEntriesAsync(request, cancellationToken);
        return Json(result);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> HarRequestDetails([FromForm] string? requestKey, CancellationToken cancellationToken)
    {
        var model = await _logAnalysisService.GetHarRequestDetailsAsync(requestKey, cancellationToken);
        return PartialView("_HarRequestDetails", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestFormLimits(MultipartBodyLengthLimit = 268_435_456)]
    public async Task<IActionResult> GenerateHarPackage(HarValidationPageViewModel model, CancellationToken cancellationToken)
    {
        var export = await _logAnalysisService.GenerateHarDashboardPackageAsync(
            model.Filter,
            model.Filter.SelectedRequestKey,
            HarDashboardExportFormat.Zip,
            cancellationToken);
        if (!export.Success || export.Content.Length == 0)
        {
            return BadRequest(export.ErrorMessage ?? "Unable to generate the dashboard reconstruction package.");
        }

        return File(export.Content, export.ContentType, export.FileName);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestFormLimits(MultipartBodyLengthLimit = 268_435_456)]
    public async Task<IActionResult> GenerateHarBbix(HarValidationPageViewModel model, CancellationToken cancellationToken)
    {
        var export = await _logAnalysisService.GenerateHarDashboardPackageAsync(
            model.Filter,
            model.Filter.SelectedRequestKey,
            HarDashboardExportFormat.Bbix,
            cancellationToken);
        if (!export.Success || export.Content.Length == 0)
        {
            return BadRequest(export.ErrorMessage ?? "Unable to generate the dashboard BBIX package.");
        }

        return File(export.Content, export.ContentType, export.FileName);
    }

    [HttpGet]
    public async Task<IActionResult> RawView([FromQuery] RawLogViewFilter filter, CancellationToken cancellationToken)
    {
        var model = await _logAnalysisService.GetRawLogViewAsync(filter, cancellationToken);
        return View(model);
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
