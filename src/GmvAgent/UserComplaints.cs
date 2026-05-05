using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;

public sealed record Complaint(
    string BuildingId,
    string BuildingName,
    string Category,
    string Description,
    DateTime CreatedAt);

public sealed class ComplaintsService
{
    private readonly AppSecrets _secrets;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMongoCollection<BsonDocument> _complaints;

    public ComplaintsService(AppSecrets secrets, IHttpClientFactory httpClientFactory)
    {
        _secrets = secrets;
        _httpClientFactory = httpClientFactory;

        if (string.IsNullOrWhiteSpace(secrets.MongoConnectionString))
        {
            throw new InvalidOperationException("MONGODB_CONNECTION_STRING is not configured.");
        }

        var client = new MongoClient(secrets.MongoConnectionString);
        _complaints = client.GetDatabase("housing_db").GetCollection<BsonDocument>("complaints");
    }

    public async Task<IReadOnlyList<Complaint>> SearchComplaintsAsync(string question, int limit)
    {
        limit = Math.Clamp(limit, 1, 10);

        // Try semantic search first if Voyage API is configured
        if (!string.IsNullOrWhiteSpace(_secrets.VoyageApiKey))
        {
            try
            {
                var queryEmbedding = await EmbedTextAsync(question);
                return await SemanticSearchAsync(queryEmbedding, limit);
            }
            catch
            {
                // Fall back to keyword search if embedding fails
            }
        }

        // Fallback: keyword search
        return await KeywordSearchAsync(question, limit);
    }

    private async Task<IReadOnlyList<double>> EmbedTextAsync(string text)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.voyageai.com/v1/embeddings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secrets.VoyageApiKey);

        var payload = JsonSerializer.Serialize(new
        {
            input = new[] { text },
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

    private async Task<IReadOnlyList<Complaint>> SemanticSearchAsync(IReadOnlyList<double> queryEmbedding, int limit)
    {
        // Fetch all complaints (or use pagination if collection is huge)
        var allComplaints = await _complaints.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();

        if (allComplaints.Count == 0)
            return Array.Empty<Complaint>();

        // Score each complaint based on similarity to query embedding
        var complaintScores = new List<(BsonDocument doc, double score)>();

        foreach (var doc in allComplaints)
        {
            try
            {
                var complaintText = BuildComplaintSearchText(doc);
                var complaintEmbedding = await EmbedTextAsync(complaintText);
                var similarity = CosineSimilarity(queryEmbedding, complaintEmbedding);
                complaintScores.Add((doc, similarity));
            }
            catch
            {
                // Skip complaints that can't be embedded
            }
        }

        // Return top N by similarity score
        return complaintScores
            .OrderByDescending(x => x.score)
            .Take(limit)
            .Select(x => ToBsonComplaint(x.doc))
            .ToList();
    }

    private async Task<IReadOnlyList<Complaint>> KeywordSearchAsync(string question, int limit)
    {
        var lowerQuestion = question.ToLower();
        var filter = Builders<BsonDocument>.Filter.Or(
            Builders<BsonDocument>.Filter.Regex("description", new BsonRegularExpression(Regex.Escape(lowerQuestion), "i")),
            Builders<BsonDocument>.Filter.Regex("category", new BsonRegularExpression(Regex.Escape(lowerQuestion), "i")),
            Builders<BsonDocument>.Filter.Regex("building_name", new BsonRegularExpression(Regex.Escape(lowerQuestion), "i"))
        );

        var docs = await _complaints
            .Find(filter)
            .Sort(Builders<BsonDocument>.Sort.Descending("created_at"))
            .Limit(limit)
            .ToListAsync();

        return docs.Select(ToBsonComplaint).ToList();
    }

    private static string BuildComplaintSearchText(BsonDocument doc)
    {
        var category = doc.GetValue("category", "").AsString;
        var description = doc.GetValue("description", "").AsString;
        var buildingName = doc.GetValue("building_name", "").AsString;
        return $"{category} {description} {buildingName}".Trim();
    }

    private static double CosineSimilarity(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count != b.Count)
            return 0;

        double dotProduct = 0;
        double magnitudeA = 0;
        double magnitudeB = 0;

        for (int i = 0; i < a.Count; i++)
        {
            dotProduct += a[i] * b[i];
            magnitudeA += a[i] * a[i];
            magnitudeB += b[i] * b[i];
        }

        magnitudeA = Math.Sqrt(magnitudeA);
        magnitudeB = Math.Sqrt(magnitudeB);

        if (magnitudeA == 0 || magnitudeB == 0)
            return 0;

        return dotProduct / (magnitudeA * magnitudeB);
    }

    private static Complaint ToBsonComplaint(BsonDocument doc)
    {
        return new Complaint(
            BuildingId: doc.GetValue("building_id", "").AsString,
            BuildingName: doc.GetValue("building_name", "").AsString,
            Category: doc.GetValue("category", "").AsString,
            Description: doc.GetValue("description", "").AsString,
            CreatedAt: doc.GetValue("created_at", BsonDateTime.Create(DateTime.UtcNow)).ToUniversalTime());
    }
}
