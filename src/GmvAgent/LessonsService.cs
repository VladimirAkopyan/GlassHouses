using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Driver;

// LessonsService: the adaptive layer.
//
// - Records every chat (question, retrieved passages, answer) into agent_memory.chat_history.
// - Optionally takes a thumbs rating from the user.
// - Asynchronously reflects on each rated/high-score chat via Claude to extract a transferable
//   lesson, embeds the lesson's question_pattern via Voyage, and stores it in agent_memory.lessons.
// - On subsequent chats, retrieves the top-K relevant lessons by cosine similarity (in-memory —
//   the lesson collection stays small enough that a vector index is overkill) and returns them
//   so /api/chat can apply them to query expansion AND to Claude's system prompt.
public sealed class LessonsService
{
    private readonly AppSecrets _secrets;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMongoCollection<BsonDocument> _lessons;
    private readonly IMongoCollection<BsonDocument> _chatHistory;

    public LessonsService(AppSecrets secrets, IHttpClientFactory httpClientFactory)
    {
        _secrets = secrets;
        _httpClientFactory = httpClientFactory;

        var client = new MongoClient(secrets.MongoConnectionString);
        var db = client.GetDatabase(secrets.MongoDatabase);
        _lessons = db.GetCollection<BsonDocument>("lessons");
        _chatHistory = db.GetCollection<BsonDocument>("chat_history");
    }

    // ---------- chat recording ----------

    public async Task<string> RecordChatAsync(
        string question,
        IReadOnlyList<double>? questionEmbedding,
        IReadOnlyList<RetrievedPassage> passages,
        string answer,
        bool usedLessonsMode,
        IReadOnlyList<string> appliedLessonIds,
        double avgScoreWithoutLessons)
    {
        var topScore = passages.Count > 0 ? passages.Max(p => p.Score) : 0.0;
        var doc = new BsonDocument
        {
            ["building_id"] = _secrets.BuildingId,
            ["question"] = question,
            ["question_embedding"] = questionEmbedding is null
                ? BsonNull.Value
                : new BsonArray(questionEmbedding.Select(v => (BsonValue)new BsonDouble(v))),
            ["retrieved_passages"] = new BsonArray(passages.Select(p => new BsonDocument
            {
                ["source_id"] = p.SourceId,
                ["source_type"] = p.SourceType ?? (BsonValue)BsonNull.Value,
                ["chunk_index"] = p.ChunkIndex,
                ["score"] = p.Score
            })),
            ["top_score"] = topScore,
            ["num_results"] = passages.Count,
            ["answer"] = answer,
            ["lessons_applied"] = new BsonArray(appliedLessonIds),
            ["used_lessons_mode"] = usedLessonsMode,
            ["avg_score_without_lessons_baseline"] = avgScoreWithoutLessons,
            ["rating"] = BsonNull.Value,
            ["reflected_at"] = BsonNull.Value,
            ["created_at"] = DateTime.UtcNow
        };

        await _chatHistory.InsertOneAsync(doc);
        return doc.GetValue("_id").AsObjectId.ToString();
    }

    public async Task<bool> SaveFeedbackAsync(string chatId, string feedback)
    {
        if (!ObjectId.TryParse(chatId, out var oid)) return false;
        var trimmed = feedback.Trim();
        if (trimmed.Length == 0) return false;
        var update = Builders<BsonDocument>.Update
            .Set("feedback", trimmed)
            .Set("rated_at", DateTime.UtcNow)
            .Set("reflected_at", BsonNull.Value);  // allow re-reflection with the new feedback
        var result = await _chatHistory.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", oid), update);
        return result.MatchedCount == 1;
    }

    // ---------- baseline tracking (for the per-answer footer) ----------

    public async Task<double> GetAvgScoreWithoutLessonsAsync()
    {
        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("used_lessons_mode", false),
            Builders<BsonDocument>.Filter.Gt("top_score", 0.0));
        var docs = await _chatHistory.Find(filter).Project(Builders<BsonDocument>.Projection.Include("top_score")).ToListAsync();
        if (docs.Count == 0)
        {
            return 0.0;
        }
        return docs.Average(d => d.GetValue("top_score", 0.0).ToDouble());
    }

    // ---------- lesson lookup ----------

    public async Task<IReadOnlyList<LessonRecord>> GetRelevantLessonsAsync(
        IReadOnlyList<double> questionEmbedding, int topK = 3, double minSim = 0.3)
    {
        var all = await _lessons.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        var ranked = new List<(LessonRecord lesson, double sim)>();
        foreach (var doc in all)
        {
            var emb = doc.GetValue("question_pattern_embedding", new BsonArray()).AsBsonArray;
            if (emb.Count != questionEmbedding.Count) continue;
            var lessonVec = emb.Select(v => v.ToDouble()).ToArray();
            var sim = CosineSimilarity(questionEmbedding, lessonVec);
            if (sim < minSim) continue;
            ranked.Add((LessonRecord.FromBson(doc), sim));
        }
        return ranked
            .OrderByDescending(x => x.sim)
            .Take(topK)
            .Select(x => x.lesson with { Similarity = x.sim })
            .ToList();
    }

    public async Task<IReadOnlyList<LessonRecord>> ListAllLessonsAsync(int limit = 200)
    {
        var docs = await _lessons.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Descending("updated_at"))
            .Limit(limit)
            .ToListAsync();
        return docs.Select(LessonRecord.FromBson).ToList();
    }

    public async Task<long> ClearAllLessonsAsync()
    {
        var result = await _lessons.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
        return result.DeletedCount;
    }

    public async Task RecordLessonApplicationsAsync(IEnumerable<string> lessonIds, double topScoreAchieved)
    {
        foreach (var id in lessonIds)
        {
            if (!ObjectId.TryParse(id, out var oid)) continue;
            var lesson = await _lessons.Find(Builders<BsonDocument>.Filter.Eq("_id", oid)).FirstOrDefaultAsync();
            if (lesson is null) continue;
            var prevApplied = lesson.GetValue("applied_count", 0).ToInt32();
            var prevAvg = lesson.GetValue("avg_score_when_applied", 0.0).ToDouble();
            var newApplied = prevApplied + 1;
            var newAvg = ((prevAvg * prevApplied) + topScoreAchieved) / newApplied;
            await _lessons.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", oid),
                Builders<BsonDocument>.Update
                    .Set("applied_count", newApplied)
                    .Set("avg_score_when_applied", newAvg)
                    .Set("updated_at", DateTime.UtcNow));
        }
    }

    // ---------- reflection (the learning step) ----------

    public async Task<LessonRecord?> ReflectAndStoreLessonAsync(string chatId)
    {
        if (!ObjectId.TryParse(chatId, out var oid)) return null;
        var chat = await _chatHistory.Find(Builders<BsonDocument>.Filter.Eq("_id", oid)).FirstOrDefaultAsync();
        if (chat is null) return null;
        if (!chat.GetValue("reflected_at", BsonNull.Value).IsBsonNull) return null;

        var feedbackVal = chat.GetValue("feedback", BsonNull.Value);
        var feedback = feedbackVal.IsBsonNull ? null : feedbackVal.AsString;
        var topScore = chat.GetValue("top_score", 0.0).ToDouble();

        // No feedback + low top score → nothing to learn from. Skip and mark reflected so we don't retry.
        if (feedback is null && topScore < 0.5)
        {
            await MarkReflected(oid);
            return null;
        }

        var question = chat.GetValue("question", "").AsString;
        var answer = chat.GetValue("answer", "").AsString;
        var passages = chat.GetValue("retrieved_passages", new BsonArray()).AsBsonArray;

        var extracted = await CallClaudeForLessonAsync(question, answer, passages, feedback);
        if (extracted is null || !extracted.ShouldSave)
        {
            await MarkReflected(oid);
            return null;
        }

        // Embed lesson patterns as "query" so they live in the same vector space as user questions
        // (which we also embed as "query"). Mixing query+document spaces deflates cosine similarity.
        var patternEmbedding = await EmbedTextAsync(extracted.QuestionPattern, "query");
        var lessonDoc = new BsonDocument
        {
            ["building_id"] = _secrets.BuildingId,
            ["question_pattern"] = extracted.QuestionPattern,
            ["question_pattern_embedding"] = new BsonArray(patternEmbedding.Select(v => (BsonValue)new BsonDouble(v))),
            ["lesson_text"] = extracted.LessonText,
            ["suggested_query_terms"] = new BsonArray(extracted.SuggestedQueryTerms),
            ["suggested_source_types"] = new BsonArray(extracted.SuggestedSourceTypes),
            ["source_chat_ids"] = new BsonArray(new[] { (BsonValue)oid }),
            ["applied_count"] = 0,
            ["avg_score_when_applied"] = 0.0,
            ["feedback_count"] = feedback is null ? 0 : 1,
            ["sample_feedback"] = feedback ?? (BsonValue)BsonNull.Value,
            ["created_at"] = DateTime.UtcNow,
            ["updated_at"] = DateTime.UtcNow
        };
        await _lessons.InsertOneAsync(lessonDoc);
        await MarkReflected(oid);
        return LessonRecord.FromBson(lessonDoc);
    }

    public async Task<int> LearnFromHistoryAsync(int maxChats = 20)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("reflected_at", BsonNull.Value);
        var docs = await _chatHistory.Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Descending("created_at"))
            .Limit(maxChats)
            .ToListAsync();
        var lessonsCreated = 0;
        foreach (var d in docs)
        {
            var id = d.GetValue("_id").AsObjectId.ToString();
            var lesson = await ReflectAndStoreLessonAsync(id);
            if (lesson is not null) lessonsCreated++;
        }
        return lessonsCreated;
    }

    private async Task MarkReflected(ObjectId oid)
    {
        await _chatHistory.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", oid),
            Builders<BsonDocument>.Update.Set("reflected_at", DateTime.UtcNow));
    }

    // ---------- LLM + embedding helpers ----------

    public async Task<IReadOnlyList<double>> EmbedTextAsync(string text, string inputType = "query")
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.voyageai.com/v1/embeddings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secrets.VoyageApiKey);
        var payload = JsonSerializer.Serialize(new
        {
            input = new[] { text },
            model = _secrets.VoyageModel,
            input_type = inputType
        });
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        var result = await JsonSerializer.DeserializeAsync<VoyageEmbeddingResponse>(stream);
        var embedding = result?.Data?.FirstOrDefault()?.Embedding ?? throw new InvalidOperationException("Voyage returned no embedding.");
        return embedding;
    }

    private async Task<ExtractedLesson?> CallClaudeForLessonAsync(string question, string answer, BsonArray passages, string? feedback)
    {
        var feedbackBlock = string.IsNullOrWhiteSpace(feedback) ? "(no user feedback was provided)" : $"\"{feedback}\"";
        var passageSummary = string.Join("\n", passages.Take(5).Select((p, i) =>
        {
            var d = p.AsBsonDocument;
            return $"  [{i + 1}] source_id={d.GetValue("source_id", "").AsString} source_type={d.GetValue("source_type", BsonNull.Value)} score={d.GetValue("score", 0.0).ToDouble():0.000}";
        }));

        var prompt = $$"""
You are analyzing a RAG chatbot's retrieval performance to extract a transferable lesson that will help retrieve better evidence for similar future questions.

Question: "{{question}}"

Top retrieved passages:
{{passageSummary}}

Final answer (truncated): "{{(answer.Length > 600 ? answer[..600] + "..." : answer)}}"

User feedback on this answer: {{feedbackBlock}}

Extract one transferable lesson. The user's feedback is the strongest signal — if they said the answer was wrong, missed something, or pointed at a better source, encode that in suggested_query_terms / suggested_source_types so the next similar question retrieves better.

Return ONLY a JSON object (no prose, no code fences) with this exact schema:
{
  "should_save": true | false,
  "question_pattern": "<short generalised form of the question, max 12 words>",
  "suggested_query_terms": ["<3 to 8 keywords or phrases that would help retrieve relevant chunks>"],
  "suggested_source_types": ["<subset of: lease, companies_house, forum, sales_data, notes, other>"],
  "lesson_text": "<one-sentence lesson, max 25 words, written as actionable retrieval guidance>"
}

Set should_save=false ONLY if the question is too generic to generalise from, or no clear retrieval pattern emerges. Vague feedback like "good" or "bad" without specifics still counts — extract a lesson from the question + retrieval pattern.
""";

        var body = JsonSerializer.Serialize(new
        {
            model = _secrets.AnthropicModel,
            max_tokens = 400,
            temperature = 0.0,
            system = "You are a careful RAG-system reflection assistant. You return strict JSON only.",
            messages = new[] { new { role = "user", content = prompt } }
        });

        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", _secrets.AnthropicApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        var responseText = await response.Content.ReadAsStringAsync();
        var parsed = JsonSerializer.Deserialize<ClaudeMessageResponse>(responseText);
        var raw = parsed?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Trim possible code fences just in case
        var jsonStart = raw.IndexOf('{');
        var jsonEnd = raw.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd <= jsonStart) return null;
        var json = raw[jsonStart..(jsonEnd + 1)];

        try
        {
            return JsonSerializer.Deserialize<ExtractedLesson>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });
        }
        catch
        {
            return null;
        }
    }

    private static double CosineSimilarity(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denom = Math.Sqrt(normA) * Math.Sqrt(normB);
        return denom == 0 ? 0 : dot / denom;
    }
}

public sealed record LessonRecord(
    string Id,
    string QuestionPattern,
    string LessonText,
    IReadOnlyList<string> SuggestedQueryTerms,
    IReadOnlyList<string> SuggestedSourceTypes,
    int AppliedCount,
    double AvgScoreWhenApplied,
    int FeedbackCount,
    string? SampleFeedback,
    DateTime CreatedAt,
    double Similarity = 0.0)
{
    public static LessonRecord FromBson(BsonDocument doc)
    {
        var sampleFeedbackVal = doc.GetValue("sample_feedback", BsonNull.Value);
        return new LessonRecord(
            Id: doc.GetValue("_id").AsObjectId.ToString(),
            QuestionPattern: doc.GetValue("question_pattern", "").AsString,
            LessonText: doc.GetValue("lesson_text", "").AsString,
            SuggestedQueryTerms: doc.GetValue("suggested_query_terms", new BsonArray()).AsBsonArray
                .Select(v => v.AsString).ToList(),
            SuggestedSourceTypes: doc.GetValue("suggested_source_types", new BsonArray()).AsBsonArray
                .Select(v => v.AsString).ToList(),
            AppliedCount: doc.GetValue("applied_count", 0).ToInt32(),
            AvgScoreWhenApplied: doc.GetValue("avg_score_when_applied", 0.0).ToDouble(),
            FeedbackCount: doc.GetValue("feedback_count", doc.GetValue("positive_feedback_count", 0)).ToInt32(),
            SampleFeedback: sampleFeedbackVal.IsBsonNull ? null : sampleFeedbackVal.AsString,
            CreatedAt: doc.GetValue("created_at", DateTime.MinValue).ToUniversalTime());
    }
}

internal sealed class ExtractedLesson
{
    [JsonPropertyName("should_save")] public bool ShouldSave { get; set; }
    [JsonPropertyName("question_pattern")] public string QuestionPattern { get; set; } = "";
    [JsonPropertyName("suggested_query_terms")] public List<string> SuggestedQueryTerms { get; set; } = [];
    [JsonPropertyName("suggested_source_types")] public List<string> SuggestedSourceTypes { get; set; } = [];
    [JsonPropertyName("lesson_text")] public string LessonText { get; set; } = "";
}
