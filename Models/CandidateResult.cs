namespace Shortlister.API.Models;

public class CandidateResult
{
    public string Filename { get; set; } = string.Empty;
    public string YearsOfExperience { get; set; } = string.Empty;
    public List<string> MissingSkills { get; set; } = new();
    public int MatchScore { get; set; }
    public string BriefReasoning { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty; // "Yes", "No", "Maybe"
    public double Distance { get; set; }
}
