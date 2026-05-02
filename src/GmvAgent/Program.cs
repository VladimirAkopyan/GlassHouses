using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddSingleton(AppSecrets.Load());
builder.Services.AddSingleton<RetrievalService>();
builder.Services.AddSingleton<ClaudeService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", async (RetrievalService retrieval, AppSecrets secrets) =>
{
    var status = await retrieval.GetStatusAsync();
    return Results.Ok(new
    {
        ok = true,
        mongo = status,
        claudeConfigured = !string.IsNullOrWhiteSpace(secrets.AnthropicApiKey),
        vectorRetrievalConfigured = !string.IsNullOrWhiteSpace(secrets.VoyageApiKey)
    });
});

app.MapPost("/api/chat", async (ChatRequest request, RetrievalService retrieval, ClaudeService claude) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        return Results.BadRequest(new { error = "Question is required." });
    }

    var passages = await retrieval.SearchAsync(request.Question, request.Limit ?? 8);
    var answer = await claude.AnswerAsync(request.Question, passages);

    return Results.Ok(new ChatResponse(answer, passages));
});

app.Run();

public sealed record ChatRequest(string Question, int? Limit);
public sealed record ChatResponse(string Answer, IReadOnlyList<RetrievedPassage> Sources);

public sealed record RetrievedPassage(
    string SourceId,
    string? SourceType,
    int ChunkIndex,
    string? Filename,
    int? Page,
    double Score,
    string Text);

public sealed class AppSecrets
{
    public string MongoConnectionString { get; init; } = "";
    public string MongoDatabase { get; init; } = "agent_memory";
    public string MongoCollection { get; init; } = "documents";
    public string MongoVectorIndex { get; init; } = "embedding_vector_cosine_1024";
    public string BuildingId { get; init; } = "gmv";
    public string AnthropicApiKey { get; init; } = "";
    public string AnthropicModel { get; init; } = "claude-sonnet-4-20250514";
    public string VoyageApiKey { get; init; } = "";
    public string VoyageModel { get; init; } = "voyage-3.5";

    public static AppSecrets Load()
    {
        var fileValues = FindApiKeysFile();

        return new AppSecrets
        {
            MongoConnectionString = Read("MONGODB_CONNECTION_STRING", fileValues, "mongoDB"),
            MongoDatabase = Read("MONGODB_DATABASE", fileValues, "mongoDatabase", "agent_memory"),
            MongoCollection = Read("MONGODB_COLLECTION", fileValues, "mongoCollection", "documents"),
            MongoVectorIndex = Read("MONGODB_VECTOR_INDEX", fileValues, "mongoVectorIndex", "embedding_vector_cosine_1024"),
            BuildingId = Read("BUILDING_ID", fileValues, "buildingId", "gmv"),
            AnthropicApiKey = Read("ANTHROPIC_API_KEY", fileValues, "claud-api-key"),
            AnthropicModel = Read("ANTHROPIC_MODEL", fileValues, "anthropicModel", "claude-sonnet-4-20250514"),
            VoyageApiKey = Read("VOYAGE_API_KEY", fileValues, "voyage-api-key"),
            VoyageModel = Read("VOYAGE_MODEL", fileValues, "voyageModel", "voyage-3.5")
        };
    }

    private static string Read(string envName, Dictionary<string, string> fileValues, string fileName, string fallback = "")
    {
        var env = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        return fileValues.TryGetValue(fileName, out var value) ? value : fallback;
    }

    private static Dictionary<string, string> FindApiKeysFile()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "APiKeys.json");
            if (File.Exists(candidate))
            {
                return ReadApiKeys(candidate);
            }
            current = current.Parent;
        }

        var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "APiKeys.json");
        return File.Exists(cwdCandidate) ? ReadApiKeys(cwdCandidate) : new Dictionary<string, string>();
    }

    private static Dictionary<string, string> ReadApiKeys(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "");
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }
}

public sealed class RetrievalService
{
    private readonly AppSecrets _secrets;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMongoCollection<BsonDocument> _documents;

    public RetrievalService(AppSecrets secrets, IHttpClientFactory httpClientFactory)
    {
        _secrets = secrets;
        _httpClientFactory = httpClientFactory;

        if (string.IsNullOrWhiteSpace(secrets.MongoConnectionString))
        {
            throw new InvalidOperationException("MONGODB_CONNECTION_STRING is not configured.");
        }

        var client = new MongoClient(secrets.MongoConnectionString);
        _documents = client.GetDatabase(secrets.MongoDatabase).GetCollection<BsonDocument>(secrets.MongoCollection);
    }

    public async Task<object> GetStatusAsync()
    {
        var count = await _documents.EstimatedDocumentCountAsync();
        return new
        {
            database = _secrets.MongoDatabase,
            collection = _secrets.MongoCollection,
            documentCount = count,
            vectorIndex = _secrets.MongoVectorIndex,
            retrievalMode = string.IsNullOrWhiteSpace(_secrets.VoyageApiKey) ? "keyword fallback" : "vector search"
        };
    }

    public async Task<IReadOnlyList<RetrievedPassage>> SearchAsync(string question, int limit)
    {
        limit = Math.Clamp(limit, 3, 15);

        if (!string.IsNullOrWhiteSpace(_secrets.VoyageApiKey))
        {
            try
            {
                var queryVector = await EmbedQueryAsync(question);
                return await VectorSearchAsync(queryVector, limit);
            }
            catch
            {
                // Keep the chatbot useful if the embedding provider is not configured correctly.
            }
        }

        return await KeywordSearchAsync(question, limit);
    }

    private async Task<IReadOnlyList<double>> EmbedQueryAsync(string question)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.voyageai.com/v1/embeddings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secrets.VoyageApiKey);

        var payload = JsonSerializer.Serialize(new
        {
            input = new[] { question },
            model = _secrets.VoyageModel,
            input_type = "query"
        });
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync<VoyageEmbeddingResponse>(stream);
        var embedding = result?.Data?.FirstOrDefault()?.Embedding;

        if (embedding is null || embedding.Count == 0)
        {
            throw new InvalidOperationException("Voyage did not return an embedding.");
        }

        return embedding;
    }

    private async Task<IReadOnlyList<RetrievedPassage>> VectorSearchAsync(IReadOnlyList<double> queryVector, int limit)
    {
        var vector = new BsonArray(queryVector.Select(value => new BsonDouble(value)));
        var vectorSearch = new BsonDocument
        {
            ["index"] = _secrets.MongoVectorIndex,
            ["path"] = "embedding",
            ["queryVector"] = vector,
            ["numCandidates"] = Math.Max(100, limit * 25),
            ["limit"] = limit
        };

        if (!string.IsNullOrWhiteSpace(_secrets.BuildingId))
        {
            vectorSearch["filter"] = new BsonDocument("building_id", _secrets.BuildingId);
        }

        var pipeline = new[]
        {
            new BsonDocument("$vectorSearch", vectorSearch),
            new BsonDocument("$project", new BsonDocument
            {
                ["source_id"] = 1,
                ["source_type"] = 1,
                ["chunk_index"] = 1,
                ["chunk_text"] = 1,
                ["metadata"] = 1,
                ["score"] = new BsonDocument("$meta", "vectorSearchScore")
            })
        };

        var docs = await _documents.Aggregate<BsonDocument>(pipeline).ToListAsync();
        return docs.Select(ToPassage).ToList();
    }

    private async Task<IReadOnlyList<RetrievedPassage>> KeywordSearchAsync(string question, int limit)
    {
        var filter = new BsonDocument("$text", new BsonDocument("$search", question));
        var projection = new BsonDocument
        {
            ["source_id"] = 1,
            ["source_type"] = 1,
            ["chunk_index"] = 1,
            ["chunk_text"] = 1,
            ["metadata"] = 1,
            ["score"] = new BsonDocument("$meta", "textScore")
        };

        var docs = await _documents.Find(filter)
            .Project(projection)
            .Sort(new BsonDocument("score", new BsonDocument("$meta", "textScore")))
            .Limit(limit)
            .ToListAsync();

        return docs.Select(ToPassage).ToList();
    }

    private static RetrievedPassage ToPassage(BsonDocument doc)
    {
        var metadata = doc.GetValue("metadata", new BsonDocument()).AsBsonDocument;
        var pageValue = metadata.GetValue("page", BsonNull.Value);
        int? page = pageValue.IsBsonNull ? null : pageValue.ToInt32();

        return new RetrievedPassage(
            SourceId: doc.GetValue("source_id", "").AsString,
            SourceType: doc.GetValue("source_type", BsonNull.Value).IsBsonNull ? null : doc.GetValue("source_type").AsString,
            ChunkIndex: doc.GetValue("chunk_index", 0).ToInt32(),
            Filename: metadata.GetValue("filename", BsonNull.Value).IsBsonNull ? null : metadata.GetValue("filename").AsString,
            Page: page,
            Score: doc.GetValue("score", 0.0).ToDouble(),
            Text: doc.GetValue("chunk_text", "").AsString);
    }
}

public sealed class ClaudeService
{
    private readonly AppSecrets _secrets;
    private readonly IHttpClientFactory _httpClientFactory;

    public ClaudeService(AppSecrets secrets, IHttpClientFactory httpClientFactory)
    {
        _secrets = secrets;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> AnswerAsync(string question, IReadOnlyList<RetrievedPassage> passages)
    {
        if (string.IsNullOrWhiteSpace(_secrets.AnthropicApiKey))
        {
            return "Claude is not configured. Set ANTHROPIC_API_KEY and ask again.";
        }

        var context = BuildContext(passages);
        var userPrompt = $"""
        Question:
        {question}

        Retrieved evidence:
        {context}

        Answer the question using only the retrieved evidence. If the evidence is thin or contradictory, say so plainly.
        Cite sources inline using [source_id#chunk_index] after the sentence they support.
        """;

        var body = JsonSerializer.Serialize(new
        {
            model = _secrets.AnthropicModel,
            max_tokens = 1200,
            temperature = 0.2,
            system = "You are a careful due-diligence assistant for Greenwich Millennium Village. You answer from retrieved source chunks, distinguish evidence from inference, and avoid legal or financial advice.",
            messages = new[]
            {
                new { role = "user", content = userPrompt }
            }
        });

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", _secrets.AnthropicApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            return $"Claude API error {(int)response.StatusCode}: {responseText}";
        }

        var parsed = JsonSerializer.Deserialize<ClaudeMessageResponse>(responseText);
        return parsed?.Content?.FirstOrDefault(c => c.Type == "text")?.Text
            ?? "Claude returned no text content.";
    }

    private static string BuildContext(IReadOnlyList<RetrievedPassage> passages)
    {
        if (passages.Count == 0)
        {
            return "No passages retrieved.";
        }

        var sb = new StringBuilder();
        foreach (var passage in passages)
        {
            sb.AppendLine($"[{passage.SourceId}#{passage.ChunkIndex}] file={passage.Filename ?? "unknown"} page={passage.Page?.ToString() ?? "unknown"} score={passage.Score:0.000}");
            sb.AppendLine(passage.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

public sealed class VoyageEmbeddingResponse
{
    [JsonPropertyName("data")]
    public List<VoyageEmbeddingData> Data { get; set; } = [];
}

public sealed class VoyageEmbeddingData
{
    [JsonPropertyName("embedding")]
    public List<double> Embedding { get; set; } = [];
}

public sealed class ClaudeMessageResponse
{
    [JsonPropertyName("content")]
    public List<ClaudeContent> Content { get; set; } = [];
}

public sealed class ClaudeContent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}
