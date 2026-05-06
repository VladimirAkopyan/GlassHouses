using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddSingleton(AppSecrets.Load());
builder.Services.AddSingleton<BuildingRegistry>();
builder.Services.AddSingleton<RetrievalService>();
builder.Services.AddSingleton<ClaudeService>();
builder.Services.AddSingleton<LessonsService>();

var app = builder.Build();

// Make sure the keyword text index exists so hybrid search has a keyword side. No-op if present.
_ = app.Services.GetRequiredService<RetrievalService>().EnsureKeywordIndexAsync();
// Best-effort: create the Atlas vector index on lessons. If creation fails or the index isn't
// ready yet, lesson lookup degrades to in-memory cosine until Atlas finishes building it.
_ = app.Services.GetRequiredService<LessonsService>().EnsureLessonsVectorIndexAsync();
// Seed curated lessons (e.g. risk-report format) so they survive lesson wipes.
_ = app.Services.GetRequiredService<LessonsService>().EnsureSeededLessonsAsync();

app.UseDefaultFiles();
var staticFileOptions = new StaticFileOptions
{
    ContentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider(
        new Dictionary<string, string>(
            new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider().Mappings)
        {
            [".geojson"] = "application/geo+json"
        })
};
app.UseStaticFiles(staticFileOptions);

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

// Serve original source documents so citations can hyperlink directly to the file.
// Walks up from the running binary to find the Findings-gmv directory; matches on basename
// only and uses Path.GetFileName to defuse any path-traversal attempts in the URL.
app.MapGet("/api/source/{filename}", (string filename) =>
{
    var safe = Path.GetFileName(filename);
    if (string.IsNullOrWhiteSpace(safe)) return Results.BadRequest();

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    string? root = null;
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "Findings-gmv");
        if (Directory.Exists(candidate)) { root = candidate; break; }
        dir = dir.Parent;
    }
    if (root is null) return Results.NotFound(new { error = "Findings-gmv directory not found on server." });

    var found = Directory.EnumerateFiles(root, safe, SearchOption.AllDirectories).FirstOrDefault();
    if (found is null) return Results.NotFound(new { error = $"File not found: {safe}" });

    var ext = Path.GetExtension(found).ToLowerInvariant();
    var contentType = ext switch
    {
        ".pdf"  => "application/pdf",
        ".html" => "text/html; charset=utf-8",
        ".md"   => "text/markdown; charset=utf-8",
        ".csv"  => "text/csv; charset=utf-8",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".txt"  => "text/plain; charset=utf-8",
        _       => "application/octet-stream"
    };
    return Results.File(found, contentType, fileDownloadName: null, enableRangeProcessing: true);
});

app.MapPost("/api/chat", async (ChatRequest request, RetrievalService retrieval, ClaudeService claude, LessonsService lessons, BuildingRegistry buildings) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        return Results.BadRequest(new { error = "Question is required." });
    }

    var useLessons = request.UseLessons ?? true;
    var limit = request.Limit ?? 8;

    // Detect named GMV buildings in the user's question. If found, every retrieval below
    // is scoped to chunks that mention one of those buildings (via the `buildings` array
    // populated at ingestion time). Lets "tell me about Etherington Lodge" actually return
    // Etherington-specific evidence rather than blending across all 40 buildings.
    var detectedBuildings = buildings.Detect(request.Question);

    // Embed the question once for lesson lookup. Retrieval embeddings are produced
    // per-query inside the agentic loop (Claude generates the queries, we embed each).
    IReadOnlyList<double>? queryEmbedding = null;
    try
    {
        queryEmbedding = await lessons.EmbedTextAsync(request.Question, "query");
    }
    catch
    {
        // Lesson lookup degrades gracefully — agentic search still works without it.
    }

    IReadOnlyList<LessonRecord> relevantLessons = useLessons && queryEmbedding is not null
        ? await lessons.GetRelevantLessonsAsync(queryEmbedding)
        : Array.Empty<LessonRecord>();

    // Agentic retrieval loop. Each search Claude issues:
    //   1. Embed the query Claude wrote (input_type=query)
    //   2. Run hybrid search (vector + keyword RRF) with source_type + building filters
    //   3. Apply relevance floor (≥0.5 vector OR ≤3 keyword rank, fallback to top 2)
    // The building filter is fixed per request (detected from the user's question, not
    // changeable by Claude) so every reformulation stays scoped to the right building(s).
    var buildingFilter = detectedBuildings.Count > 0 ? detectedBuildings : null;
    Func<string, IReadOnlyList<string>?, Task<IReadOnlyList<RetrievedPassage>>> searchTool =
        async (query, sourceTypes) =>
        {
            try
            {
                var emb = await lessons.EmbedTextAsync(query, "query");
                var hits = await retrieval.HybridSearchAsync(query, emb, limit, sourceTypes, buildingFilter);
                if (hits.Count == 0 && buildingFilter is not null)
                {
                    // The building filter starved the results — fall back without it so the
                    // user gets *something* (Claude can still note the building wasn't found).
                    hits = await retrieval.HybridSearchAsync(query, emb, limit, sourceTypes);
                }
                return ApplyRelevanceFloor(hits);
            }
            catch
            {
                return Array.Empty<RetrievedPassage>();
            }
        };

    var agentic = await claude.AnswerWithToolsAsync(request.Question, relevantLessons, searchTool);
    var passages = agentic.Passages;
    var answer = agentic.Answer;

    var avgBaseline = await lessons.GetAvgScoreWithoutLessonsAsync();
    var topScore = passages.Count > 0 ? passages.Max(p => p.Score) : 0.0;
    var appliedIds = relevantLessons.Select(l => l.Id).ToList();

    var chatId = await lessons.RecordChatAsync(
        request.Question, queryEmbedding, passages, answer, useLessons, appliedIds, avgBaseline);

    if (relevantLessons.Count > 0)
    {
        _ = lessons.RecordLessonApplicationsAsync(appliedIds, topScore);
    }

    var primaryLocation = GmvPlaces.FindPrimary(request.Question, agentic.SearchQueries, answer);

    return Results.Ok(new ChatResponse(
        Answer: answer,
        Sources: passages,
        ChatId: chatId,
        AppliedLessons: relevantLessons,
        TopScore: topScore,
        AvgScoreBaseline: avgBaseline,
        NewLesson: (LessonRecord?)null,
        UsedLessonsMode: useLessons,
        SearchQueries: agentic.SearchQueries,
        PrimaryLocation: primaryLocation,
        DetectedBuildings: detectedBuildings));
});

static IReadOnlyList<RetrievedPassage> ApplyRelevanceFloor(IReadOnlyList<RetrievedPassage> hits)
{
    const double minVectorScore = 0.5;
    const int maxKeywordRank = 3;
    var filtered = hits.Where(p =>
        (p.VectorScore.HasValue && p.VectorScore.Value >= minVectorScore) ||
        (p.KeywordRank.HasValue && p.KeywordRank.Value <= maxKeywordRank)
    ).ToList();
    if (filtered.Count < 2 && hits.Count > 0)
    {
        filtered = hits.Take(Math.Min(2, hits.Count)).ToList();
    }
    return filtered;
}

app.MapPost("/api/rate", async (RateRequest request, LessonsService lessons) =>
{
    if (string.IsNullOrWhiteSpace(request.ChatId))
    {
        return Results.BadRequest(new { error = "chat_id is required." });
    }
    if (request.Rating is null || request.Rating < 1 || request.Rating > 5)
    {
        return Results.BadRequest(new { error = "rating (1-5) is required." });
    }
    var ok = await lessons.SaveFeedbackAsync(request.ChatId, request.Rating.Value, request.Feedback);
    if (!ok) return Results.BadRequest(new { error = "Invalid chat_id or rating." });
    var lesson = await lessons.ReflectAndStoreLessonAsync(request.ChatId);
    return Results.Ok(new { ok = true, newLesson = lesson });
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

public sealed record ChatRequest(string Question, int? Limit, bool? UseLessons);
public sealed record ChatResponse(
    string Answer,
    IReadOnlyList<RetrievedPassage> Sources,
    string ChatId,
    IReadOnlyList<LessonRecord> AppliedLessons,
    double TopScore,
    double AvgScoreBaseline,
    LessonRecord? NewLesson,
    bool UsedLessonsMode,
    IReadOnlyList<string> SearchQueries,
    PlaceInfo? PrimaryLocation,
    IReadOnlyList<string> DetectedBuildings);

public sealed record PlaceInfo(string Name, double Lat, double Lng, string Label);

// Approximate coordinates for known places mentioned in GMV chats. Sub-building positions
// are best-effort eyeballed within the development; the OpenStreetMap base layer makes
// this visually grounded enough for a demo without needing surveyed accuracy.
public static class GmvPlaces
{
    private static readonly (string Name, double Lat, double Lng, string Label)[] Known =
    {
        ("Holly Court",                  51.4960, 0.0085, "Holly Court, GMV"),
        ("Farnsworth Court",             51.4980, 0.0070, "Farnsworth Court, GMV"),
        ("Renaissance Walk",             51.4965, 0.0090, "Renaissance Walk, GMV"),
        ("Greenwich Millennium Village", 51.4970, 0.0080, "Greenwich Millennium Village"),
        ("GMV",                          51.4970, 0.0080, "Greenwich Millennium Village"),
        ("Greenwich Peninsula",          51.5000, 0.0050, "Greenwich Peninsula"),
        ("Greenwich",                    51.4826, 0.0077, "Greenwich (London Borough)")
    };

    // Picks the most specific (longest-name) match across the question, search queries, and answer.
    // Heavy weight on the user's question — that's the topic anchor, even if the answer drifts.
    public static PlaceInfo? FindPrimary(string question, IReadOnlyList<string> queries, string answer)
    {
        var allText = $"{question}\n{string.Join("\n", queries)}\n{answer}".ToLowerInvariant();
        var questionLower = question.ToLowerInvariant();

        // Longest first → "Holly Court" beats "Greenwich"; "Greenwich Millennium Village" beats "Greenwich".
        foreach (var p in Known.OrderByDescending(p => p.Name.Length))
        {
            var needle = p.Name.ToLowerInvariant();
            if (questionLower.Contains(needle)) return new PlaceInfo(p.Name, p.Lat, p.Lng, p.Label);
        }
        foreach (var p in Known.OrderByDescending(p => p.Name.Length))
        {
            var needle = p.Name.ToLowerInvariant();
            if (allText.Contains(needle)) return new PlaceInfo(p.Name, p.Lat, p.Lng, p.Label);
        }
        return null;
    }
}
public sealed record RateRequest(string? ChatId, int? Rating, string? Feedback);

public sealed record RetrievedPassage(
    string SourceId,
    string? SourceType,
    int ChunkIndex,
    string? Filename,
    int? Page,
    double Score,
    string Text,
    double? VectorScore = null,
    int? KeywordRank = null,
    double RrfScore = 0.0);

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

    public async Task<IReadOnlyList<RetrievedPassage>> SearchWithEmbeddingAsync(
        IReadOnlyList<double> queryVector, int limit, IReadOnlyCollection<string>? sourceTypes = null)
    {
        limit = Math.Clamp(limit, 3, 15);
        return await VectorSearchAsync(queryVector, limit, sourceTypes);
    }

    // Hybrid retrieval: vector + keyword fused with Reciprocal Rank Fusion (RRF, k=60).
    // Each side oversamples (limit*2) so the fusion has room to surface chunks that one
    // method missed. Returns up to `limit` passages sorted by RrfScore. Each passage carries:
    //  - VectorScore: original cosine score from Atlas if found via vector (else null)
    //  - KeywordRank: 1-indexed rank in keyword results if found there (else null)
    //  - Score:       VectorScore when available, else 0.0 (drives the existing UI / topScore metric)
    //  - RrfScore:    fused rank score used for sorting/merging across pipelines
    // If keyword search fails (e.g. no $text index), degrades to vector-only.
    public async Task<IReadOnlyList<RetrievedPassage>> HybridSearchAsync(
        string questionText,
        IReadOnlyList<double> queryVector,
        int limit,
        IReadOnlyCollection<string>? sourceTypes = null,
        IReadOnlyCollection<string>? buildings = null)
    {
        limit = Math.Clamp(limit, 3, 15);
        var oversample = limit * 2;

        var vectorTask = VectorSearchAsync(queryVector, oversample, sourceTypes, buildings);
        var keywordTask = TryKeywordSearchAsync(questionText, oversample, buildings);
        await Task.WhenAll(vectorTask, keywordTask);

        var vectorResults = vectorTask.Result;
        var keywordResults = keywordTask.Result;

        const double k = 60.0;
        var fused = new Dictionary<string, (RetrievedPassage passage, double rrf, double? vScore, int? kRank)>();

        for (int i = 0; i < vectorResults.Count; i++)
        {
            var p = vectorResults[i];
            var key = $"{p.SourceId}#{p.ChunkIndex}";
            var rrf = 1.0 / (k + (i + 1));
            fused[key] = (p, rrf, p.Score, null);
        }
        for (int i = 0; i < keywordResults.Count; i++)
        {
            var p = keywordResults[i];
            var key = $"{p.SourceId}#{p.ChunkIndex}";
            var rrf = 1.0 / (k + (i + 1));
            if (fused.TryGetValue(key, out var existing))
            {
                fused[key] = (existing.passage, existing.rrf + rrf, existing.vScore, i + 1);
            }
            else
            {
                fused[key] = (p, rrf, null, i + 1);
            }
        }

        return fused.Values
            .OrderByDescending(x => x.rrf)
            .Take(limit)
            .Select(x => x.passage with
            {
                Score = x.vScore ?? 0.0,
                VectorScore = x.vScore,
                KeywordRank = x.kRank,
                RrfScore = x.rrf
            })
            .ToList();
    }

    private async Task<IReadOnlyList<RetrievedPassage>> TryKeywordSearchAsync(
        string question, int limit, IReadOnlyCollection<string>? buildings = null)
    {
        try
        {
            return await KeywordSearchAsync(question, limit, buildings);
        }
        catch
        {
            // Most likely: no $text index on the collection. Hybrid degrades to vector-only.
            return Array.Empty<RetrievedPassage>();
        }
    }

    public async Task EnsureKeywordIndexAsync()
    {
        try
        {
            var existing = await _documents.Indexes.ListAsync();
            var indexes = await existing.ToListAsync();
            if (indexes.Any(ix => ix.GetValue("name", "").AsString == "chunk_text_text")) return;

            var keys = new BsonDocument("chunk_text", "text");
            var options = new CreateIndexOptions<BsonDocument> { Name = "chunk_text_text" };
            var model = new CreateIndexModel<BsonDocument>(keys, options);
            await _documents.Indexes.CreateOneAsync(model);
        }
        catch
        {
            // Non-fatal — hybrid search degrades to vector-only.
        }
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

    private async Task<IReadOnlyList<RetrievedPassage>> VectorSearchAsync(
        IReadOnlyList<double> queryVector,
        int limit,
        IReadOnlyCollection<string>? sourceTypes = null,
        IReadOnlyCollection<string>? buildings = null)
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

        var filterClauses = new BsonArray();
        if (!string.IsNullOrWhiteSpace(_secrets.BuildingId))
        {
            // Schema v2: every chunk has development_id="gmv". building_id is now the
            // specific building (e.g. "farnsworth_court") or DEVELOPMENT_WIDE / LBSM_UNMAPPED
            // sentinels. The chatbot's top-level filter is the development.
            filterClauses.Add(new BsonDocument("development_id", _secrets.BuildingId));
        }
        if (sourceTypes is { Count: > 0 })
        {
            filterClauses.Add(new BsonDocument("source_type",
                new BsonDocument("$in", new BsonArray(sourceTypes))));
        }
        if (buildings is { Count: > 0 })
        {
            // Match chunks where any of the named buildings appears in the buildings array.
            filterClauses.Add(new BsonDocument("buildings",
                new BsonDocument("$in", new BsonArray(buildings))));
        }
        if (filterClauses.Count == 1)
        {
            vectorSearch["filter"] = filterClauses[0].AsBsonDocument;
        }
        else if (filterClauses.Count > 1)
        {
            vectorSearch["filter"] = new BsonDocument("$and", filterClauses);
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

    private async Task<IReadOnlyList<RetrievedPassage>> KeywordSearchAsync(
        string question, int limit, IReadOnlyCollection<string>? buildings = null)
    {
        // development_id filter mirrors the vector path; without it we'd surface chunks
        // from other developments if the corpus ever grows beyond gmv.
        var clauses = new BsonArray { new BsonDocument("$text", new BsonDocument("$search", question)) };
        if (!string.IsNullOrWhiteSpace(_secrets.BuildingId))
        {
            clauses.Add(new BsonDocument("development_id", _secrets.BuildingId));
        }
        if (buildings is { Count: > 0 })
        {
            clauses.Add(new BsonDocument("buildings",
                new BsonDocument("$in", new BsonArray(buildings))));
        }
        var filter = clauses.Count == 1 ? clauses[0].AsBsonDocument : new BsonDocument("$and", clauses);

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

public sealed record AgenticAnswer(
    string Answer,
    IReadOnlyList<RetrievedPassage> Passages,
    IReadOnlyList<string> SearchQueries);

public sealed class ClaudeService
{
    private readonly AppSecrets _secrets;
    private readonly IHttpClientFactory _httpClientFactory;

    public ClaudeService(AppSecrets secrets, IHttpClientFactory httpClientFactory)
    {
        _secrets = secrets;
        _httpClientFactory = httpClientFactory;
    }

    // Agentic retrieval: Claude orchestrates the search via tool use.
    // It can call search_documents up to maxIterations times, reformulating between calls,
    // then answers from whatever it found. Lessons are surfaced as system-prompt guidance —
    // Claude decides how to use them rather than us forcing them into the query.
    public async Task<AgenticAnswer> AnswerWithToolsAsync(
        string question,
        IReadOnlyList<LessonRecord>? appliedLessons,
        Func<string, IReadOnlyList<string>?, Task<IReadOnlyList<RetrievedPassage>>> searchTool,
        int maxIterations = 3)
    {
        if (string.IsNullOrWhiteSpace(_secrets.AnthropicApiKey))
        {
            return new AgenticAnswer("Claude is not configured. Set ANTHROPIC_API_KEY and ask again.",
                Array.Empty<RetrievedPassage>(), Array.Empty<string>());
        }

        var systemPrompt = BuildAgenticSystemPrompt(appliedLessons);
        var tools = BuildToolDefinitions();

        var messages = new List<object>
        {
            new { role = "user", content = question }
        };

        var queriesUsed = new List<string>();
        var allPassages = new Dictionary<string, RetrievedPassage>();
        string? finalText = null;

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            var requestBody = JsonSerializer.Serialize(new
            {
                model = _secrets.AnthropicModel,
                max_tokens = 1500,
                temperature = 0.2,
                system = systemPrompt,
                tools,
                messages
            });

            var responseJson = await SendAnthropicRequestAsync(requestBody);
            if (responseJson is null)
            {
                finalText = "Claude API error — see server logs.";
                break;
            }

            // Parse the response: collect text and tool_use blocks.
            var contentBlocks = responseJson.RootElement.TryGetProperty("content", out var contentEl)
                ? contentEl.EnumerateArray().ToList()
                : new List<JsonElement>();
            var stopReason = responseJson.RootElement.TryGetProperty("stop_reason", out var srEl)
                ? srEl.GetString() : null;

            var toolUseBlocks = contentBlocks
                .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "tool_use")
                .ToList();

            // No tool calls → Claude is answering. Extract its text and finish.
            if (toolUseBlocks.Count == 0 || stopReason != "tool_use")
            {
                finalText = string.Join("\n",
                    contentBlocks
                        .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text")
                        .Select(b => b.GetProperty("text").GetString() ?? ""));
                break;
            }

            // Append the assistant turn (raw content blocks — we have to echo them back verbatim).
            messages.Add(new { role = "assistant", content = contentBlocks.Select(JsonElementToObject).ToList() });

            // Execute each tool call, collect tool_result blocks for the next user turn.
            var toolResults = new List<object>();
            foreach (var block in toolUseBlocks)
            {
                var toolUseId = block.GetProperty("id").GetString() ?? "";
                var toolName = block.GetProperty("name").GetString() ?? "";
                var input = block.GetProperty("input");

                if (toolName == "search_documents")
                {
                    var query = input.TryGetProperty("query", out var qEl) ? qEl.GetString() ?? "" : "";
                    IReadOnlyList<string>? sourceTypes = null;
                    if (input.TryGetProperty("source_types", out var stEl) && stEl.ValueKind == JsonValueKind.Array)
                    {
                        sourceTypes = stEl.EnumerateArray()
                            .Select(e => e.GetString() ?? "")
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .ToList();
                        if (sourceTypes.Count == 0) sourceTypes = null;
                    }

                    queriesUsed.Add(query);
                    var hits = await searchTool(query, sourceTypes);

                    foreach (var p in hits)
                    {
                        var key = $"{p.SourceId}#{p.ChunkIndex}";
                        if (!allPassages.TryGetValue(key, out var existing) || p.RrfScore > existing.RrfScore)
                        {
                            allPassages[key] = p;
                        }
                    }

                    var resultText = FormatHitsForClaude(hits);
                    toolResults.Add(new
                    {
                        type = "tool_result",
                        tool_use_id = toolUseId,
                        content = resultText
                    });
                }
                else
                {
                    toolResults.Add(new
                    {
                        type = "tool_result",
                        tool_use_id = toolUseId,
                        content = $"Unknown tool: {toolName}",
                        is_error = true
                    });
                }
            }

            messages.Add(new { role = "user", content = toolResults });
        }

        if (finalText is null)
        {
            // Hit the iteration cap without a final text. Force a wrap-up turn.
            messages.Add(new
            {
                role = "user",
                content = "You've used your search budget. Answer now from the evidence you've already retrieved."
            });
            var wrapBody = JsonSerializer.Serialize(new
            {
                model = _secrets.AnthropicModel,
                max_tokens = 1500,
                temperature = 0.2,
                system = systemPrompt,
                messages
            });
            var wrapResponse = await SendAnthropicRequestAsync(wrapBody);
            finalText = wrapResponse?.RootElement.TryGetProperty("content", out var c) == true
                ? string.Join("\n", c.EnumerateArray()
                    .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text")
                    .Select(b => b.GetProperty("text").GetString() ?? ""))
                : "Out of search budget and Claude returned no final text.";
        }

        return new AgenticAnswer(
            Answer: finalText,
            Passages: allPassages.Values.OrderByDescending(p => p.RrfScore).ToList(),
            SearchQueries: queriesUsed);
    }

    private async Task<JsonDocument?> SendAnthropicRequestAsync(string body)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", _secrets.AnthropicApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Claude API error {(int)response.StatusCode}: {text}");
            return null;
        }
        return JsonDocument.Parse(text);
    }

    private static string FormatHitsForClaude(IReadOnlyList<RetrievedPassage> hits)
    {
        if (hits.Count == 0) return "No matching chunks found. Try a different query — different keywords, building names, or terminology.";
        // Truncate each chunk to ~600 chars: enough for Claude to judge relevance and quote, but
        // small enough to keep latency reasonable across multiple iterations. The full text is
        // preserved in the API response for the UI/citations.
        const int maxChars = 600;
        var sb = new StringBuilder();
        sb.AppendLine($"Found {hits.Count} chunks:");
        sb.AppendLine();
        foreach (var p in hits)
        {
            var vScore = p.VectorScore.HasValue ? p.VectorScore.Value.ToString("0.000") : "—";
            var kRank = p.KeywordRank.HasValue ? p.KeywordRank.Value.ToString() : "—";
            sb.AppendLine($"[{p.SourceId}#{p.ChunkIndex}] type={p.SourceType ?? "?"} vScore={vScore} kRank={kRank} file={p.Filename ?? "?"} page={p.Page?.ToString() ?? "?"}");
            var snippet = p.Text.Length > maxChars ? p.Text[..maxChars] + "…" : p.Text;
            sb.AppendLine(snippet);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildAgenticSystemPrompt(IReadOnlyList<LessonRecord>? lessons)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a careful due-diligence assistant for Greenwich Millennium Village (a London apartment development, development_id \"gmv\"). The development contains 40 named buildings split between GMV West and GMV East; each chunk you retrieve has a building_id (a slug like \"farnsworth_court\" or \"holly_court\") and a primary_building name, plus a buildings array listing every named building mentioned in the chunk. When the user asks about a specific building, name it explicitly in your answer. You answer questions for building service providers using a corpus of documents about this development.");
        sb.AppendLine();
        sb.AppendLine("Corpus contents: leases, Companies House filings, forum threads, sales data, notes. The corpus references specific buildings (Holly Court, Farnsworth Court, etc.) and uses terms like \"GMV\" or \"Greenwich Millennium Village\" — it never uses the phrase \"Glass Houses\" even though users may ask that way.");
        sb.AppendLine();
        sb.AppendLine("You have a `search_documents` tool. To answer well:");
        sb.AppendLine("- Search before answering. Use specific keywords, proper nouns, and the corpus's actual terminology.");
        sb.AppendLine("- If the user asks about \"Glass Houses\", search for \"GMV\", \"Greenwich Millennium Village\", or specific building names instead.");
        sb.AppendLine("- If a search returns thin or off-topic results, search again with different terms. Reformulate aggressively.");
        sb.AppendLine("- You can call search_documents up to 3 times. Choose queries strategically — usually one well-formed query is enough.");
        sb.AppendLine("- Stop searching once you have enough relevant evidence, then answer using only retrieved chunks.");
        sb.AppendLine("- If after searching you genuinely have no evidence, say so plainly — do not fabricate.");
        sb.AppendLine();
        sb.AppendLine("ANSWER STYLE — be concise and well-formatted:");
        sb.AppendLine("- Lead with a one-sentence direct answer (a short prose paragraph).");
        sb.AppendLine("- THEN, when you have 3+ findings, list them as bullets — ONE finding per line, each line starting with a dash and a space: \"- ...\".");
        sb.AppendLine("- Each bullet is ONE sentence. Do NOT combine multiple findings on one line. Do NOT run findings together in a paragraph.");
        sb.AppendLine("- Do NOT use **bold sub-headings** like \"**Key Positives:**\" or \"**Concerns:**\". The emojis below already convey that. Just go straight to the bullets.");
        sb.AppendLine("- Do NOT use section headings (no \"## Heading\", no \"###\"). Do NOT use asterisks for bullets, only dashes.");
        sb.AppendLine("- Use **bold** sparingly inside a bullet for the single key term (e.g. **£4.75 million**, **Holly Court**). Never bold the whole bullet.");
        sb.AppendLine("- Cite sources inline as [source_id#chunk_index] only on claims that need backing.");
        sb.AppendLine("- No preamble (\"Based on my search…\"), no recap of the question, no closing summary.");
        sb.AppendLine();
        sb.AppendLine("EVALUATIVE EMOJI — every bullet that makes a judgment about a finding starts with exactly one emoji directly after the dash:");
        sb.AppendLine("- ✅  positive / reassuring");
        sb.AppendLine("- ⚠️  caution / risk worth flagging");
        sb.AppendLine("- ❌  negative / blocking issue");
        sb.AppendLine("- ℹ️  neutral context (no judgment)");
        sb.AppendLine();
        sb.AppendLine("EXAMPLE of the EXACT format expected:");
        sb.AppendLine("Greenwich Millennium Village has strong fundamentals but a flagged litigation history.");
        sb.AppendLine();
        sb.AppendLine("- ✅ **Strong transport links** via A102 and North Greenwich Tube [GMV_Phases#24].");
        sb.AppendLine("- ❌ **£4.75M flooding lawsuit** against Essex Services Group [legal-dispute-map#6].");
        sb.AppendLine("- ⚠️ Multi-year **service charge dispute** at Holly Court resulted in a 2008 tribunal refund [legal-dispute-map#10].");
        sb.AppendLine("- ℹ️ Development was built in phases starting in 2002 [GLA_Report#0].");
        sb.AppendLine();
        sb.AppendLine("Each bullet is one sentence, one emoji, one source citation max. Follow this format every time you have findings to list.");
        sb.AppendLine();
        sb.AppendLine("You distinguish evidence from inference. You do not give legal or financial advice.");

        if (lessons is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("Learned guidance from past similar questions (apply when relevant):");
            foreach (var l in lessons)
            {
                sb.AppendLine($"- {l.LessonText}");
                if (l.SuggestedQueryTerms.Count > 0)
                    sb.AppendLine($"  (suggested terms to try: {string.Join(", ", l.SuggestedQueryTerms)})");
                if (l.SuggestedSourceTypes.Count > 0)
                    sb.AppendLine($"  (suggested source types to filter to: {string.Join(", ", l.SuggestedSourceTypes)})");
            }
        }

        return sb.ToString();
    }

    private static object[] BuildToolDefinitions()
    {
        return new object[]
        {
            new
            {
                name = "search_documents",
                description = "Search the GMV document corpus for chunks relevant to a query. Returns top chunks with text, source, and scores. Call multiple times with reformulated queries to find better evidence.",
                input_schema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new
                        {
                            type = "string",
                            description = "The search query — use specific keywords, proper nouns (Holly Court, Farnsworth Court, GMV), and corpus terminology rather than the user's wording."
                        },
                        source_types = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "string",
                                @enum = new[] { "lease", "companies_house", "forum", "sales_data", "notes", "other" }
                            },
                            description = "Optional. Restrict the search to specific source types. Leave empty to search across all."
                        }
                    },
                    required = new[] { "query" }
                }
            }
        };
    }

    // Anthropic requires us to echo assistant content blocks back verbatim, including unknown
    // fields. JsonElement → plain object preserves them across the serializer round-trip.
    private static object JsonElementToObject(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
            JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.String => el.GetString() ?? "",
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : (object)el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null!,
            _ => ""
        };
    }

    public async Task<string> AnswerAsync(
        string question,
        IReadOnlyList<RetrievedPassage> passages,
        IReadOnlyList<LessonRecord>? appliedLessons = null)
    {
        if (string.IsNullOrWhiteSpace(_secrets.AnthropicApiKey))
        {
            return "Claude is not configured. Set ANTHROPIC_API_KEY and ask again.";
        }

        var context = BuildContext(passages);
        var lessonsBlock = BuildLessonsBlock(appliedLessons);
        var userPrompt = $"""
        Question:
        {question}

        Retrieved evidence:
        {context}

        Answer the question using only the retrieved evidence. If the evidence is thin or contradictory, say so plainly.
        Cite sources inline using [source_id#chunk_index] after the sentence they support.
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
