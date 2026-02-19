using System.Text.RegularExpressions;

namespace Shortlister.API.Services;

public class GoogleDriveService
{
    private readonly HttpClient _httpClient;

    public GoogleDriveService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromMinutes(5); // Increase timeout to handle slow downloads
    }

    public async Task<Stream?> DownloadFileAsync(string url)
    {
        var fileId = ExtractFileId(url);
        if (string.IsNullOrEmpty(fileId))
        {
            Console.WriteLine($"[Error] Could not extract File ID from URL: {url}");
            return null;
        }

        // Try direct download URL first
        var downloadUrl = $"https://drive.google.com/uc?export=download&id={fileId}";

        try
        {
            var response = await _httpClient.GetAsync(downloadUrl);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStreamAsync();
            }
            
            Console.WriteLine($"[Error] Failed to download file. Status: {response.StatusCode}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Exception downloading file: {ex.Message}");
            return null;
        }
    }

    private string? ExtractFileId(string url)
    {
        // Matches /d/FILE_ID/ or id=FILE_ID
        var specificMatch = Regex.Match(url, @"/d/([a-zA-Z0-9_-]+)|id=([a-zA-Z0-9_-]+)");
        
        if (specificMatch.Success)
        {
            // Group 1 is for /d/, Group 2 is for id=
            return !string.IsNullOrEmpty(specificMatch.Groups[1].Value) 
                ? specificMatch.Groups[1].Value 
                : specificMatch.Groups[2].Value;
        }
        return null;
    }
}
