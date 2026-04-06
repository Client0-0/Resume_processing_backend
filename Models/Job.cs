namespace Shortlister.API.Models;

public class Job
{
    public Guid JobId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
