using Microsoft.SemanticKernel;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.ChatCompletion;
using UglyToad.PdfPig;
using Shortlister.API.Models;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;

namespace Shortlister.API.Services;

public class ResumeProcessingService
{
    private readonly Kernel _kernel;
#pragma warning disable SKEXP0001
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
#pragma warning restore SKEXP0001
    private readonly int _chunkWords;
    private readonly int _chunkOverlapWords;

    // Common English stop words excluded from keyword extraction
    private static readonly HashSet<string> _stopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with",
        "by", "from", "as", "is", "was", "are", "were", "be", "been", "being", "have", "has",
        "had", "do", "does", "did", "will", "would", "could", "should", "may", "might", "must",
        "can", "that", "this", "these", "those", "it", "its", "we", "you", "he", "she", "they",
        "i", "my", "your", "our", "their", "his", "her", "not", "no", "nor", "so", "yet",
        "both", "either", "neither", "each", "few", "more", "most", "other", "some", "such",
        "than", "then", "into", "through", "during", "before", "after", "above", "below",
        "between", "out", "off", "over", "under", "again", "further", "once", "about", "also"
    };

    public ResumeProcessingService(IConfiguration configuration)
    {
        var settings = configuration.GetSection("LLMSettings");
        var openAIKey =
            settings["OpenAIApiKey"]
            ?? Environment.GetEnvironmentVariable("LLMSettings__OpenAIApiKey")
            ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        var openAIModel =
            settings["OpenAIModelId"]
            ?? Environment.GetEnvironmentVariable("LLMSettings__OpenAIModelId")
            ?? "gpt-4o-mini";

        var openAIEmbeddingModel =
            settings["OpenAIEmbeddingModelId"]
            ?? Environment.GetEnvironmentVariable("LLMSettings__OpenAIEmbeddingModelId")
            ?? "text-embedding-3-small";

        var processingSettings = configuration.GetSection("ShortlisterProcessing");
        _chunkWords = processingSettings.GetValue<int?>("ChunkWords") ?? 200;
        _chunkOverlapWords = processingSettings.GetValue<int?>("ChunkOverlapWords") ?? 50;

        var builder = Kernel.CreateBuilder();

        if (string.IsNullOrWhiteSpace(openAIKey))
        {
            throw new InvalidOperationException("OpenAI API key is missing. Set LLMSettings__OpenAIApiKey (or OPENAI_API_KEY) in .env.");
        }

        // Register OpenAI chat
        builder.AddOpenAIChatCompletion(
            modelId: openAIModel,
            apiKey: openAIKey,
            serviceId: "OpenAI");

        // Register OpenAI embeddings
#pragma warning disable SKEXP0010
        builder.AddOpenAIEmbeddingGenerator(
            modelId: openAIEmbeddingModel,
            apiKey: openAIKey);
#pragma warning restore SKEXP0010

        _kernel = builder.Build();
        
        _embeddingService = _kernel.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
    }

    public string ExtractTextFromPdf(Stream pdfStream)
    {
        try 
        {
            using var document = PdfDocument.Open(pdfStream);
            var text = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                text.Append(page.Text);
            }
            return text.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error extracting text: {ex.Message}");
            return string.Empty;
        }
    }

    public string ExtractTextFromDocx(Stream docxStream)
    {
        try
        {
            using WordprocessingDocument wordDocument = WordprocessingDocument.Open(docxStream, false);
            var body = wordDocument?.MainDocumentPart?.Document?.Body;
            return body?.InnerText ?? string.Empty;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error extracting text from docx: {ex.Message}");
            return string.Empty;
        }
    }

    public async Task<List<CandidateResult>> ProcessResumesAsync(List<(string filename, string text)> resumes, string jd)
    {
        // 1. Get JD Embedding
        var truncatedJd = TruncateToWordLimit(jd, 150); // ~512 tokens safe limit
        var jdEmbeddingResult = await _embeddingService.GenerateAsync(new[] { truncatedJd });
        var jdVector = jdEmbeddingResult[0].Vector;

        // 1b. Extract JD keywords for hybrid Layer 1 search (keyword arm)
        var jdKeywords = ExtractJdKeywords(jd);

        var results = new List<CandidateResult>();

        // STEP 1: Filter Resumes using "Vector Search" (Cosine Similarity)
        var qualifiedResumes = new List<(string filename, string text, double similarity)>();

        foreach (var (filename, text) in resumes)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Semantic Chunking: Split resume into chunks (200 words)
            // Increased from 100 to 200 to capture slightly more context per segment while staying within 512 token limit.
            var chunks = ChunkText(text, _chunkWords, _chunkOverlapWords);
            double maxSimilarity = 0;
            
            foreach (var chunk in chunks)
            {
                var chunkEmbeddingResult = await _embeddingService.GenerateAsync(new[] { chunk });
                var chunkVector = chunkEmbeddingResult[0].Vector;
                var similarity = CosineSimilarity(jdVector.ToArray(), chunkVector.ToArray());
                
                if (similarity > maxSimilarity)
                {
                    maxSimilarity = similarity;
                }
            }
            
            // Hybrid Layer 1 score: 60% semantic + 40% keyword
            // - Semantic catches conceptual alignment even when terminology differs
            // - Keyword anchors domain-specific skills/tech that embeddings may dilute
            var keywordScore = CalculateKeywordScore(text, jdKeywords);
            var hybridScore = (0.6 * maxSimilarity) + (0.4 * keywordScore);
            var distance = 1.0 - maxSimilarity;

            Console.WriteLine($"[DEBUG] File: {filename} | Semantic: {maxSimilarity:F4} | Keyword: {keywordScore:F4} | Hybrid: {hybridScore:F4} | Threshold: > 0.25");

            // Hybrid Filter Logic
            if (hybridScore > 0.25)
            {
                Console.WriteLine($"[DEBUG] -> PASSED Filter 1 (Hybrid)");
                qualifiedResumes.Add((filename, text, hybridScore));
            }
            else
            {
                Console.WriteLine($"[DEBUG] -> REJECTED by Filter 1 (Low Hybrid Score)");
                results.Add(new CandidateResult {
                    Filename = filename,
                    Decision = "No",
                    BriefReasoning = $"Filtered out by hybrid search. Semantic: {maxSimilarity:F2}, Keyword: {keywordScore:F2}, Hybrid: {hybridScore:F2} (Low relevance).",
                    MatchScore = (int)(hybridScore * 100),
                    Distance = distance
                });
            }
        }

        // STEP 2: LLM Analysis for qualified candidates
        // System prompt defined once (outside the loop) — used for every candidate
        // Separating persona/instructions into a system message gives GPT models
        // a privileged, role-level context that the user message cannot override,
        // leading to more consistent JSON-only output.
        const string systemPrompt = """
            You are a senior technical recruiter with 15 years of experience evaluating candidates.
            Your task is to assess how well a candidate's resume matches a given job description.

            You MUST respond with ONLY a valid JSON object using these exact keys:
            {
                "YearsOfExperience": "string or number",
                "MissingSkills": ["skill1", "skill2"],
                "MatchScore": number (0-100),
                "Decision": "Yes" or "No" or "Maybe",
                "BriefReasoning": "Concise justification for the decision."
            }

            Rules:
            - Output ONLY the JSON object. No markdown, no explanation, no preamble.
            - Decision must be: "Yes" (strong match), "No" (poor match), or "Maybe" (borderline).
            - MatchScore must reflect genuine skills overlap, not just keyword presence.
            """;

        foreach (var candidate in qualifiedResumes)
        {
            var userMessage = $"""
                Job Description:
                {jd}

                Resume Text:
                {candidate.text}

                Evaluate this candidate against the job description and return the JSON.
                """;

            var chatHistory = new ChatHistory();
            chatHistory.AddSystemMessage(systemPrompt);
            chatHistory.AddUserMessage(userMessage);

            try
            {
                var openAIService = _kernel.GetRequiredService<IChatCompletionService>("OpenAI");
                Console.WriteLine($"[AI] Analyzing {candidate.filename} with OpenAI...");
                var response = await openAIService.GetChatMessageContentAsync(chatHistory);
                string jsonContent = response.ToString();
                
                // Robust JSON Extraction & Repair
                jsonContent = ExtractJson(jsonContent);
                jsonContent = RepairJson(jsonContent);

                if (string.IsNullOrWhiteSpace(jsonContent))
                {
                    results.Add(new CandidateResult { Filename = candidate.filename, BriefReasoning = "Failed to generate valid analysis.", Decision = "Error" });
                    continue;
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };
                var data = JsonSerializer.Deserialize<JsonElement>(jsonContent, options);
                
                var result = new CandidateResult
                {
                    Filename = candidate.filename,
                    YearsOfExperience = GetString(data, "YearsOfExperience"),
                    MissingSkills = GetStringList(data, "MissingSkills"),
                    MatchScore = GetInt(data, "MatchScore"),
                    BriefReasoning = GetString(data, "BriefReasoning"),
                    Decision = GetString(data, "Decision"),
                    Distance = 1.0 - candidate.similarity
                };
                
                results.Add(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error analyzing {candidate.filename}: {ex.Message}");
                results.Add(new CandidateResult { Filename = candidate.filename, BriefReasoning = "Error during AI analysis.", Decision = "Error" });
            }
        }

        return results.Where(r => !r.Decision.Equals("No", StringComparison.OrdinalIgnoreCase))
                      .OrderByDescending(r => r.MatchScore)
                      .ToList();
    }

    // Helper to safely extract JSON string from LLM output
    private string ExtractJson(string text)
    {
        // Find first {
        int startIndex = text.IndexOf('{');
        if (startIndex == -1) return text;
        
        // If we have a closing }, take substring. 
        // If NOT, we likely have truncated JSON. Take everything from { to end.
        int endIndex = text.LastIndexOf('}');
        
        if (endIndex != -1 && endIndex > startIndex)
        {
            return text.Substring(startIndex, endIndex - startIndex + 1);
        }
        
        return text.Substring(startIndex); 
    }

    private string RepairJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;

        // Simple stack-based repair for truncated JSON
        // Count open brackets/braces and append missing closing ones
        var stack = new Stack<char>();
        bool inString = false;
        bool escaped = false;

        foreach (char c in json)
        {
            if (c == '"' && !escaped) inString = !inString;
            if (c == '\\' && !escaped) escaped = true;
            else escaped = false;

            if (!inString)
            {
                if (c == '{') stack.Push('}');
                else if (c == '[') stack.Push(']');
                else if (c == '}' || c == ']')
                {
                    if (stack.Count > 0 && stack.Peek() == c) stack.Pop();
                }
            }
        }

        var sb = new StringBuilder(json);
        while (stack.Count > 0)
        {
            sb.Append(stack.Pop());
        }
        return sb.ToString();
    }

    private string GetString(JsonElement element, string key)
    {
        if (element.TryGetProperty(key, out var prop)) return prop.ToString();
        // Fallback for snake_case or spaces
        if (element.TryGetProperty(key.Replace(" ", ""), out prop)) return prop.ToString();
        if (element.TryGetProperty(key.Replace(" ", "_"), out prop)) return prop.ToString();
        return "N/A";
    }

    private int GetInt(JsonElement element, string key)
    {
         if (element.TryGetProperty(key, out var prop) && prop.TryGetInt32(out var val)) return val;
         return 0;
    }

    private List<string> GetStringList(JsonElement element, string key)
    {
        var list = new List<string>();
        if (element.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Array)
        {
            foreach(var item in prop.EnumerateArray())
            {
                list.Add(item.ToString());
            }
        }
        return list;
    }

    // ── Hybrid Layer 1 Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Extracts meaningful tokens from the JD for keyword matching.
    /// Keeps alphanumeric tokens + common tech symbols (+, #, .) longer than 3 chars.
    /// </summary>
    private HashSet<string> ExtractJdKeywords(string jd)
    {
        return Regex.Split(jd, @"[^a-zA-Z0-9\+\#\.]+")
                    .Where(w => w.Length > 3 && !_stopWords.Contains(w))
                    .Select(w => w.ToLowerInvariant())
                    .ToHashSet();
    }

    /// <summary>
    /// Calculates what fraction of JD keywords appear in the resume text.
    /// Returns a score in [0, 1].
    /// </summary>
    private double CalculateKeywordScore(string resumeText, HashSet<string> jdKeywords)
    {
        if (jdKeywords.Count == 0) return 0;
        var resumeLower = resumeText.ToLowerInvariant();
        int matches = jdKeywords.Count(kw => resumeLower.Contains(kw));
        return (double)matches / jdKeywords.Count;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private List<string> ChunkText(string text, int wordsPerChunk, int overlapWords)
    {
        var words = text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();

        if (wordsPerChunk <= 0)
        {
            wordsPerChunk = 200;
        }

        // Force valid overlap so sliding-window progress is guaranteed.
        overlapWords = Math.Max(0, Math.Min(overlapWords, wordsPerChunk - 1));
        var step = wordsPerChunk - overlapWords;

        for (int i = 0; i < words.Length; i += step)
        {
            var chunkWords = words.Skip(i).Take(wordsPerChunk);
            var chunk = string.Join(" ", chunkWords);
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                chunks.Add(chunk);
            }
        }
        
        if (chunks.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            chunks.Add(text);
        }
        
        return chunks;
    }

    private double CosineSimilarity(float[] vector1, float[] vector2)
    {
        double dotProduct = 0;
        double norm1 = 0;
        double norm2 = 0;
        for (int i = 0; i < vector1.Length; i++)
        {
            dotProduct += vector1[i] * vector2[i];
            norm1 += Math.Pow(vector1[i], 2);
            norm2 += Math.Pow(vector2[i], 2);
        }
         
         if (norm1 == 0 || norm2 == 0) return 0;

        return dotProduct / (Math.Sqrt(norm1) * Math.Sqrt(norm2));
    }
    private string TruncateToWordLimit(string text, int maxWords = 200)
    {
    var words = text.Split(new[] { ' ', '\r', '\n', '\t' }, 
                    StringSplitOptions.RemoveEmptyEntries);
    return words.Length <= maxWords 
        ? text 
        : string.Join(" ", words.Take(maxWords));
    }
}
