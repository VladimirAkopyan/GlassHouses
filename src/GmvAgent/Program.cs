using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Driver;
using DotNetEnv;

DotNetEnv.Env.Load();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddConsole();

builder.Services.AddHttpClient();
builder.Services.AddSingleton(AppSecrets.Load());
builder.Services.AddSingleton<RetrievalService>();
builder.Services.AddSingleton<ClaudeService>();
builder.Services.AddSingleton<LessonsService>();
builder.Services.AddSingleton<ComplaintsService>();

var app = builder.Build();

app.Logger.LogInformation("Starting app");

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

app.MapGet("/api/map-entries", async (RetrievalService retrieval) =>
{
    var points = await retrieval.GetMapPointsAsync();
    return Results.Ok(points);
});

app.MapPost("/api/chat", async (ChatRequest request, RetrievalService retrieval, ClaudeService claude, LessonsService lessons, ComplaintsService complaints) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        return Results.BadRequest(new { error = "Question is required." });
    }

    var useLessons = request.UseLessons ?? true;
    var useComplaints = request.UseComplaints ?? true;
    var limit = request.Limit ?? 8;

    // Embed once; reuse for retrieval AND lesson lookup
    IReadOnlyList<double>? queryEmbedding = null;
    try
    {
        queryEmbedding = await lessons.EmbedTextAsync(request.Question, "query");
    }
    catch
    {
        // If embedding fails, retrieval will fall back to keyword search and lessons stay empty
    }

    // Pull relevant lessons (or skip if toggle off)
    IReadOnlyList<LessonRecord> relevantLessons = useLessons && queryEmbedding is not null
        ? await lessons.GetRelevantLessonsAsync(queryEmbedding)
        : Array.Empty<LessonRecord>();

    // Apply lessons to query: append suggested terms
    var augmentedQuestion = request.Question;
    if (relevantLessons.Count > 0)
    {
        var extraTerms = relevantLessons
            .SelectMany(l => l.SuggestedQueryTerms)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8);
        augmentedQuestion = $"{request.Question} {string.Join(" ", extraTerms)}".Trim();
    }

    // Re-embed the augmented question for retrieval (so lesson terms actually shift the vector)
    IReadOnlyList<RetrievedPassage> passages;
    if (relevantLessons.Count > 0 && queryEmbedding is not null)
    {
        try
        {
            var augmentedEmbedding = await lessons.EmbedTextAsync(augmentedQuestion, "query");
            passages = await retrieval.SearchWithEmbeddingAsync(augmentedEmbedding, limit);
        }
        catch
        {
            passages = await retrieval.SearchAsync(request.Question, limit);
        }
    }
    else
    {
        passages = queryEmbedding is not null
            ? await retrieval.SearchWithEmbeddingAsync(queryEmbedding, limit)
            : await retrieval.SearchAsync(request.Question, limit);
    }

    var relevantComplaints = useComplaints
        ? await complaints.SearchComplaintsAsync(request.Question, 5)
        : Array.Empty<Complaint>();

    var answer = await claude.AnswerAsync(request.Question, passages, relevantLessons, relevantComplaints);

    var avgBaseline = await lessons.GetAvgScoreWithoutLessonsAsync();
    var topScore = passages.Count > 0 ? passages.Max(p => p.Score) : 0.0;
    var appliedIds = relevantLessons.Select(l => l.Id).ToList();

    var chatId = await lessons.RecordChatAsync(
        request.Question, queryEmbedding, passages, answer, useLessons, appliedIds, avgBaseline);

    if (relevantLessons.Count > 0)
    {
        // fire-and-forget: bump usage stats
        _ = lessons.RecordLessonApplicationsAsync(appliedIds, topScore);
    }

    // Auto-reflect (background, fire-and-forget) so the UI doesn't wait
    LessonRecord? newLesson = null;
    try
    {
        // We do this synchronously for the demo so the toast can be returned —
        // hackathon UX > production hygiene. Comment out for fully async behavior.
        newLesson = await lessons.ReflectAndStoreLessonAsync(chatId);
    }
    catch
    {
        // reflection failures should never break the chat
    }

    return Results.Ok(new ChatResponse(
        Answer: answer,
        Sources: passages,
        ChatId: chatId,
        AppliedLessons: relevantLessons,
        TopScore: topScore,
        AvgScoreBaseline: avgBaseline,
        NewLesson: newLesson,
        UsedLessonsMode: useLessons,
        Complaints: relevantComplaints));
});

app.MapPost("/api/rate", async (RateRequest request, LessonsService lessons) =>
{
    var ok = await lessons.RateChatAsync(request.ChatId ?? "", request.Rating ?? "");
    if (!ok) return Results.BadRequest(new { error = "Invalid chat_id or rating (use 'up' or 'down')." });
    // If thumbs-up, try reflecting again now that we have a positive signal
    if (request.Rating == "up")
    {
        var lesson = await lessons.ReflectAndStoreLessonAsync(request.ChatId!);
        return Results.Ok(new { ok = true, newLesson = lesson });
    }
    return Results.Ok(new { ok = true, newLesson = (LessonRecord?)null });
});

app.MapGet("/api/lessons", async (LessonsService lessons) =>
{
    var list = await lessons.ListAllLessonsAsync();
    return Results.Ok(new { count = list.Count, lessons = list });
});

app.MapPost("/api/learn-from-history", async (LessonsService lessons, int? max) =>
{
    var n = await lessons.LearnFromHistoryAsync(max ?? 20);
    return Results.Ok(new { lessonsCreated = n });
});

app.MapPost("/api/lessons/clear", async (LessonsService lessons) =>
{
    var n = await lessons.ClearAllLessonsAsync();
    return Results.Ok(new { deleted = n });
});

app.Run();

public sealed record ChatRequest(string Question, int? Limit, bool? UseLessons, bool? UseComplaints);
public sealed record ChatResponse(
    string Answer,
    IReadOnlyList<RetrievedPassage> Sources,
    string ChatId,
    IReadOnlyList<LessonRecord> AppliedLessons,
    double TopScore,
    double AvgScoreBaseline,
    LessonRecord? NewLesson,
    bool UsedLessonsMode,
    IReadOnlyList<Complaint> Complaints);
public sealed record RateRequest(string? ChatId, string? Rating);
public sealed record MapEntry(
    string SourceId,
    string? SourceType,
    int ChunkIndex,
    string? Filename,
    int? Page,
    double? Latitude,
    double? Longitude);
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

    public async Task<IReadOnlyList<MapEntry>> GetMapPointsAsync()
    {
        var filter = string.IsNullOrWhiteSpace(_secrets.BuildingId)
            ? FilterDefinition<BsonDocument>.Empty
            : Builders<BsonDocument>.Filter.Eq("building_id", _secrets.BuildingId);

        var projection = Builders<BsonDocument>.Projection
            .Include("source_id")
            .Include("source_type")
            .Include("chunk_index")
            .Include("metadata");

        var docs = await _documents.Find(filter)
            .Project(projection)
            .ToListAsync();

        return docs
            .Select(ToMapEntry)
            .Where(entry => entry.Latitude.HasValue && entry.Longitude.HasValue)
            .ToList();
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

    public async Task<IReadOnlyList<RetrievedPassage>> SearchWithEmbeddingAsync(IReadOnlyList<double> queryVector, int limit)
    {
        limit = Math.Clamp(limit, 3, 15);
        return await VectorSearchAsync(queryVector, limit);
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

    private static MapEntry ToMapEntry(BsonDocument doc)
    {
        var metadata = doc.GetValue("metadata", new BsonDocument()).AsBsonDocument;
        var pageValue = metadata.GetValue("page", BsonNull.Value);
        int? page = pageValue.IsBsonNull ? null : pageValue.ToInt32();

        var latitude = ExtractCoordinate(metadata, "latitude", "lat", "location.latitude", "location.lat");
        var longitude = ExtractCoordinate(metadata, "longitude", "lng", "lon", "location.longitude", "location.lng");

        if (!latitude.HasValue || !longitude.HasValue)
        {
            var arrayCoords = GetCoordinatesFromArray(metadata, "coordinates", "location.coordinates");
            if (arrayCoords.latitude.HasValue && arrayCoords.longitude.HasValue)
            {
                latitude = latitude ?? arrayCoords.latitude;
                longitude = longitude ?? arrayCoords.longitude;
            }
        }

        return new MapEntry(
            SourceId: doc.GetValue("source_id", "").AsString,
            SourceType: doc.GetValue("source_type", BsonNull.Value).IsBsonNull ? null : doc.GetValue("source_type").AsString,
            ChunkIndex: doc.GetValue("chunk_index", 0).ToInt32(),
            Filename: metadata.GetValue("filename", BsonNull.Value).IsBsonNull ? null : metadata.GetValue("filename").AsString,
            Page: page,
            Latitude: latitude,
            Longitude: longitude);
    }

    private static double? ExtractCoordinate(BsonDocument metadata, params string[] paths)
    {
        foreach (var path in paths)
        {
            if (TryGetValue(metadata, path, out var value) && ParseCoordinate(value) is double parsed)
            {
                return parsed;
            }
        }

        return null;
    }

    private static (double? latitude, double? longitude) GetCoordinatesFromArray(BsonDocument metadata, params string[] paths)
    {
        foreach (var path in paths)
        {
            if (!TryGetValue(metadata, path, out var arrayValue) || !arrayValue.IsBsonArray)
            {
                continue;
            }

            var array = arrayValue.AsBsonArray;
            if (array.Count < 2) continue;

            var first = ParseCoordinate(array[0]);
            var second = ParseCoordinate(array[1]);
            if (!first.HasValue || !second.HasValue) continue;

            // Assume [lng, lat] when values look like longitude/latitude pair.
            if (Math.Abs(first.Value) > 90 && Math.Abs(second.Value) <= 90)
            {
                return (latitude: second, longitude: first);
            }

            return (latitude: first, longitude: second);
        }

        return (null, null);
    }

    private static bool TryGetValue(BsonDocument document, string path, out BsonValue value)
    {
        value = BsonNull.Value;
        var current = (BsonValue)document;
        foreach (var segment in path.Split('.'))
        {
            if (!current.IsBsonDocument || !current.AsBsonDocument.TryGetValue(segment, out current))
            {
                return false;
            }
        }

        value = current;
        return true;
    }

    private static double? ParseCoordinate(BsonValue value)
    {
        if (value.IsBsonNull) return null;
        if (value.IsDouble) return value.AsDouble;
        if (value.IsInt32) return value.AsInt32;
        if (value.IsInt64) return value.AsInt64;
        if (value.IsString && double.TryParse(value.AsString, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        return null;
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

    public async Task<string> AnswerAsync(
        string question,
        IReadOnlyList<RetrievedPassage> passages,
        IReadOnlyList<LessonRecord>? appliedLessons = null,
        IReadOnlyList<Complaint>? complaints = null)
    {
        if (string.IsNullOrWhiteSpace(_secrets.AnthropicApiKey))
        {
            return "Claude is not configured. Set ANTHROPIC_API_KEY and ask again.";
        }

        var context = BuildContext(passages);
        var lessonsBlock = BuildLessonsBlock(appliedLessons);
        var complaintsBlock = BuildComplaintsBlock(complaints);
        var userPrompt = $"""
        Question:
        {question}

        Retrieved evidence:
        {context}

        Answer the question using only the retrieved evidence. If the evidence is thin or contradictory, say so plainly.
        Cite sources inline using [source_id#chunk_index] after the sentence they support.{complaintsBlock}
        """;

        var systemPrompt = "You are a careful due-diligence assistant for Greenwich Millennium Village. You answer from retrieved source chunks, distinguish evidence from inference, and avoid legal or financial advice."
            + lessonsBlock;

        var body = JsonSerializer.Serialize(new
        {
            model = _secrets.AnthropicModel,
            max_tokens = 1200,
            temperature = 0.2,
            system = systemPrompt,
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

    private static string BuildComplaintsBlock(IReadOnlyList<Complaint>? complaints)
    {
        if (complaints is null || complaints.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("Related resident complaints from the housing management system:");
        foreach (var c in complaints)
        {
            sb.AppendLine($"- [{c.BuildingName}] {c.Category}: {c.Description}");
        }
        return sb.ToString();
    }

    private static string BuildLessonsBlock(IReadOnlyList<LessonRecord>? lessons)
    {
        if (lessons is null || lessons.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("Learned guidance from past similar questions (apply when relevant):");
        foreach (var l in lessons)
        {
            sb.AppendLine($"- {l.LessonText}");
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
