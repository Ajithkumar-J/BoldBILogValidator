namespace BoldLogValidator.Models;

public class RawLogViewFilter
{
    public bool UseLocalLogPath { get; set; } = true;

    public string LocalLogPath { get; set; } = @"C:\BoldServices\app_data\logs";

    public string? UploadSessionId { get; set; }

    public string? SelectedService { get; set; }

    public string? SelectedFile { get; set; }

    public string? SearchTerm { get; set; }

    public bool SearchAllFiles { get; set; }
}
