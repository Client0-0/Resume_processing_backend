using Microsoft.SemanticKernel;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Connectors.Ollama;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.ChatCompletion;
using UglyToad.PdfPig;
using Shortlister.API.Models;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace Shortlister.API.Services;

public class ResumeProcessingService
{
    private readonly Kernel _kernel;
#pragma warning disable SKEXP0001
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;
#pragma warning restore SKEXP0001

    public ResumeProcessingService(IConfiguration configuration)
    {
        var settings = configuration.GetSection("LLMSettings");
        var openAIKey = settings["OpenAIApiKey"];
        var openAIModel = settings["OpenAIModelId"] ?? "gpt-4o";
        
        var localModel = settings["LocalModelId"] ?? "llama3.2";
        var localEndpoint = settings["LocalEndpoint"] ?? "http://localhost:11434";
        var embeddingModelId = settings["EmbeddingModelId"] ?? "mahonzhan/all-MiniLM-L6-v2";

        var builder = Kernel.CreateBuilder();

        // Register OpenAI (Primary)
        if (!string.IsNullOrWhiteSpace(openAIKey))
        {
            builder.AddOpenAIChatCompletion(
                modelId: openAIModel,
                apiKey: openAIKey,
                serviceId: "OpenAI");
        }

        // Register Ollama (Fallback)
        builder.AddOllamaChatCompletion(
            modelId: localModel,
            endpoint: new Uri(localEndpoint),
            serviceId: "Ollama");

        // Register Embeddings (Local)
#pragma warning disable CS0618
        builder.AddOllamaEmbeddingGenerator(embeddingModelId, new Uri(localEndpoint));
#pragma warning restore CS0618

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

    public async Task<List<CandidateResult>> ProcessResumesAsync(List<(string filename, string text)> resumes, string jd)
    {
        // 1. Get JD Embedding
        var jdEmbeddingResult = await _embeddingService.GenerateAsync(new[] { jd });
        var jdVector = jdEmbeddingResult[0].Vector;

        var results = new List<CandidateResult>();

        // STEP 1: Filter Resumes using "Vector Search" (Cosine Similarity)
        var qualifiedResumes = new List<(string filename, string text, double similarity)>();

        foreach (var (filename, text) in resumes)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Semantic Chunking: Split resume into chunks (200 words)
            // Increased from 100 to 200 to capture slightly more context per segment while staying within 512 token limit.
            var chunks = ChunkText(text, 200); 
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
            
            var distance = 1.0 - maxSimilarity;
            
            Console.WriteLine($"[DEBUG] File: {filename} | Max Similarity: {maxSimilarity:F4} | Distance: {distance:F4} | Threshold: > 0.25");

            // Filter Logic: 
            if (maxSimilarity > 0.25)
            {
                Console.WriteLine($"[DEBUG] -> PASSED Filter 1");
                qualifiedResumes.Add((filename, text, maxSimilarity));
            }
            else
            {
                Console.WriteLine($"[DEBUG] -> REJECTED by Filter 1 (Low Similarity)");
                 results.Add(new CandidateResult { 
                     Filename = filename, 
                     Decision = "No", 
                     BriefReasoning = $"Filtered out by initial component match. Max Similarity: {maxSimilarity:F2} (Low relevance).",
                     MatchScore = (int)(maxSimilarity * 100),
                     Distance = distance 
                 });
            }
        }

        // STEP 2: LLM Analysis for qualified candidates
        foreach (var candidate in qualifiedResumes)
        {
            var prompt = $$"""
                Job Description:
                {{jd}}
                
                Resume Text:
                {{candidate.text}}
                
                You are a senior technical recruiter. Evaluate the candidate against the Job Description.
                
                You MUST output a valid JSON object with these exact keys:
                {
                    "YearsOfExperience": "string or number",
                    "MissingSkills": ["skill1", "skill2", "skill3"],
                    "MatchScore": number (0-100),
                    "Decision": "Yes" or "No" or "Maybe",
                    "BriefReasoning": "Concise justification for the decision."
                }
                
                Do not include any other text, markdown formatting, or explanations outside the JSON.
                JSON:
                """;

            try 
            {
                string jsonContent = string.Empty;

                // Try Primary (OpenAI)
                try
                {
                    var openAIService = _kernel.GetRequiredService<IChatCompletionService>("OpenAI");
                    Console.WriteLine($"[AI] Analyzing {candidate.filename} with OpenAI...");
                    var response = await openAIService.GetChatMessageContentAsync(prompt);
                    jsonContent = response.ToString();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Warning] OpenAI failed: {ex.Message}. Falling back to Ollama...");
                    
                    // Fallback (Ollama)
                    var ollamaService = _kernel.GetRequiredService<IChatCompletionService>("Ollama");
                    Console.WriteLine($"[AI] Analyzing {candidate.filename} with Local Llama...");
                    var response = await ollamaService.GetChatMessageContentAsync(prompt);
                    jsonContent = response.ToString();
                }
                
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

    private List<string> ChunkText(string text, int wordsPerChunk)
    {
        var words = text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();
        
        for (int i = 0; i < words.Length; i += wordsPerChunk)
        {
            var chunkWords = words.Skip(i).Take(wordsPerChunk);
            chunks.Add(string.Join(" ", chunkWords));
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
}
