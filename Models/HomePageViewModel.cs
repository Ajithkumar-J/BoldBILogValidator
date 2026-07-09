namespace BoldLogValidator.Models;

public class HomePageViewModel
{
    public AnalysisFilterInput Filter { get; set; } = new();

    public AnalysisResult? Result { get; set; }
}
