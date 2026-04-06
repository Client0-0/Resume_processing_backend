using Microsoft.AspNetCore.Mvc;
using Shortlister.API.Services;
using ExcelDataReader;
using System.Text;
using Azure.Storage.Blobs;

// Register Encoding for ExcelDataReader
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
LoadDotEnvIfPresent();

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();
builder.Services.AddControllers();
builder.Services.AddScoped<GoogleDriveService>();
builder.Services.AddScoped<ResumeProcessingService>();

// Register Azure Blob Service Client (optional for shortlist flow).
builder.Services.AddSingleton(x =>
{
    var configuration = x.GetRequiredService<IConfiguration>();
    var connectionString = configuration["AzureStorage:ConnectionString"]
                           ?? Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");

    return new AzureBlobClientAccessor
    {
        Client = string.IsNullOrWhiteSpace(connectionString)
            ? null
            : new BlobServiceClient(connectionString)
    };
});

// Enable CORS for React frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp",
        policy => policy.WithOrigins("http://localhost:5173", "http://localhost:5174", "http://127.0.0.1:5173") 
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .SetIsOriginAllowed(origin => true) // allow any origin during development just in case
                        .AllowCredentials());
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors("AllowReactApp");

app.MapPost("/api/shortlist", async (
    [FromForm] IFormFileCollection resumes,
    [FromForm] string? jd,
    [FromForm] IFormFile? jdFile,
    [FromServices] ResumeProcessingService shortlister,
    [FromServices] GoogleDriveService driveService) =>
{
    if (resumes == null || resumes.Count == 0)
    {
        return Results.BadRequest("No files uploaded.");
    }

    string finalJd = jd ?? string.Empty;

    // Optional JD file input. If provided, it takes precedence over text.
    if (jdFile is not null && jdFile.Length > 0)
    {
        try
        {
            using var jdStream = jdFile.OpenReadStream();
            var jdExtension = Path.GetExtension(jdFile.FileName).ToLowerInvariant();

            finalJd = jdExtension switch
            {
                ".txt" => await new StreamReader(jdStream).ReadToEndAsync(),
                ".pdf" => shortlister.ExtractTextFromPdf(jdStream),
                ".docx" => shortlister.ExtractTextFromDocx(jdStream),
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(finalJd))
            {
                return Results.BadRequest("Could not extract text from JD file. Supported formats: .txt, .pdf, .docx");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Processing JD file: {ex.Message}");
            return Results.BadRequest($"Error processing JD file: {ex.Message}");
        }
    }

    if (string.IsNullOrWhiteSpace(finalJd))
    {
        return Results.BadRequest("Job description is required (paste text in jd or upload jdFile).");
    }

    var resumeData = new List<(string filename, string text)>();

    foreach (var file in resumes)
    {
        var extension = Path.GetExtension(file.FileName).ToLower();

        if (extension == ".xlsx" || extension == ".xls")
        {
            // Process Excel File
            try 
            {
                using var stream = file.OpenReadStream();
                using var reader = ExcelReaderFactory.CreateReader(stream);
                var result = reader.AsDataSet(new ExcelDataSetConfiguration()
                {
                    ConfigureDataTable = (_) => new ExcelDataTableConfiguration()
                    {
                        UseHeaderRow = true
                    }
                });

                var table = result.Tables[0];
                
                // Find column index for "Candidate Link"
                int linkColumnIndex = -1;
                int nameColumnIndex = -1;

                for (int i = 0; i < table.Columns.Count; i++)
                {
                    var colName = table.Columns[i].ColumnName?.ToLower();
                    if (colName == "candidate link" || colName == "resume link" || colName == "link") linkColumnIndex = i;
                    if (colName == "candidate name" || colName == "name") nameColumnIndex = i;
                }

                if (linkColumnIndex == -1)
                {
                     return Results.BadRequest("Could not find 'Candidate Link' column in Excel file.");
                }

                Console.WriteLine($"[Excel] Found {table.Rows.Count} candidates to process...");

                foreach (System.Data.DataRow row in table.Rows)
                {
                    var link = row[linkColumnIndex]?.ToString();
                    var name = nameColumnIndex != -1 ? row[nameColumnIndex]?.ToString() : "Unknown Candidate";
                    
                    if (!string.IsNullOrWhiteSpace(link))
                    {
                        Console.WriteLine($"[Excel] Downloading resume for {name} from {link}...");
                        
                        // RATE LIMITING: Add a random delay (2-5 seconds) to avoid Google Drive 429/403 errors
                        var delayMs = new Random().Next(2000, 5000);
                        await Task.Delay(delayMs);
                        
                        var pdfStream = await driveService.DownloadFileAsync(link);
                        
                        if (pdfStream != null)
                        {
                            var text = shortlister.ExtractTextFromPdf(pdfStream);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                // Use name or fallback to a derived filename
                                var virtualFilename = !string.IsNullOrWhiteSpace(name) && name != "Unknown Candidate" 
                                    ? $"{name}.pdf" 
                                    : $"resume_{Guid.NewGuid().ToString().Substring(0,8)}.pdf";
                                    
                                resumeData.Add((virtualFilename, text));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Processing Excel: {ex.Message}");
                return Results.BadRequest($"Error processing Excel file: {ex.Message}");
            }
        }
        else if (extension == ".pdf")
        {
            // Process PDF File directly (legacy support)
            using var stream = file.OpenReadStream();
            var text = shortlister.ExtractTextFromPdf(stream);
            if (!string.IsNullOrWhiteSpace(text))
            {
                resumeData.Add((file.FileName, text));
            }
        }
        else if (extension == ".docx")
        {
            // Process DOCX File
            using var stream = file.OpenReadStream();
            var text = shortlister.ExtractTextFromDocx(stream);
            if (!string.IsNullOrWhiteSpace(text))
            {
                resumeData.Add((file.FileName, text));
            }
        }
    }

    if (resumeData.Count == 0)
    {
        return Results.BadRequest("Could not extract text from any resumes/links.");
    }

    try
    {
        var results = await shortlister.ProcessResumesAsync(resumeData, finalJd);
        return Results.Ok(results);
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"[Error] OpenAI connectivity issue: {ex.Message}");
        return Results.Problem(
            title: "OpenAI connectivity error",
            detail: "Unable to reach api.openai.com. Check internet/DNS/proxy settings and try again.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Error] Shortlisting failed: {ex.Message}");
        return Results.Problem(
            title: "Shortlisting failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }
})
.DisableAntiforgery();

app.MapPost("/api/upload-report", async (
    [FromForm] IFormFile file,
    [FromServices] AzureBlobClientAccessor blobAccessor) =>
{
    if (file == null || file.Length == 0)
    {
        return Results.BadRequest("No file uploaded.");
    }

    var blobServiceClient = blobAccessor.Client;
    if (blobServiceClient is null)
    {
        return Results.BadRequest("Azure storage is not configured. Set AZURE_STORAGE_CONNECTION_STRING or AzureStorage__ConnectionString.");
    }

    try
    {
        string containerName = "filecontainer";
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
        await containerClient.CreateIfNotExistsAsync();

        var blobClient = containerClient.GetBlobClient(file.FileName);
        
        using var stream = file.OpenReadStream();
        await blobClient.UploadAsync(stream, overwrite: true);

        Console.WriteLine($"[Azure] Uploaded {file.FileName} successfully.");
        return Results.Ok(new { message = $"File {file.FileName} uploaded successfully." });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Error] Azure Upload: {ex.Message}");
        return Results.Problem($"Error uploading file: {ex.Message}");
    }
})
.DisableAntiforgery();

app.MapControllers();

app.Run();

static void LoadDotEnvIfPresent()
{
    var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
    if (!File.Exists(envPath)) return;

    foreach (var rawLine in File.ReadAllLines(envPath))
    {
        var line = rawLine.Trim();
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0) continue;

        var key = line[..separatorIndex].Trim();
        var value = line[(separatorIndex + 1)..].Trim();

        // Strip optional surrounding quotes from .env values.
        if ((value.StartsWith('"') && value.EndsWith('"')) || (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            value = value[1..^1];
        }

        if (string.IsNullOrWhiteSpace(key)) continue;

        // Keep externally provided environment values as higher priority.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
