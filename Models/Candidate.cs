namespace Shortlister.API.Models;

public class Candidate
{
    public Guid CandidateId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string ParsedResumeText { get; set; } = string.Empty;
}
