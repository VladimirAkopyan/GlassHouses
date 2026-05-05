using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace GmvAgent.Tests;

public sealed class ComplaintsTest
{
    [Fact]
    public async Task CanReadComplaintsCollection()
    {
        LoadEnvironment();

        var connectionString = Environment.GetEnvironmentVariable("MONGODB_CONNECTION_STRING");
        var client = new MongoClient(connectionString);
        var database = client.GetDatabase("housing_db");
        var collectionNames = await database.ListCollectionNames().ToListAsync();

        Assert.Contains("complaints", collectionNames);

        var complaintsCollection = database.GetCollection<BsonDocument>("complaints");

        var complaints = await complaintsCollection
            .Find(FilterDefinition<BsonDocument>.Empty)
            .Limit(100)
            .ToListAsync();

        var renderedComplaints = complaints
            .Select(FormatComplaint)
            .ToList();

        Console.WriteLine(string.Join(Environment.NewLine + Environment.NewLine, renderedComplaints));

        Assert.NotEmpty(complaints);
        Assert.Contains(
            complaints,
            complaint =>
                complaint.GetValue("building_name", "").AsString.Contains("E1 7ND", StringComparison.OrdinalIgnoreCase) ||
                complaint.GetValue("description", "").AsString.Contains("E1 7ND", StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatComplaint(BsonDocument complaint)
    {
        var buildingId = complaint.GetValue("building_id", "").AsString;
        var buildingName = complaint.GetValue("building_name", "").AsString;
        var category = complaint.GetValue("category", "").AsString;
        var description = complaint.GetValue("description", "").AsString;
        var createdAt = complaint.GetValue("created_at", BsonNull.Value);
        var createdText = createdAt.IsBsonNull
            ? "unknown"
            : createdAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");

        return $"[{buildingId}] {buildingName}{Environment.NewLine}" +
               $"Category: {category}{Environment.NewLine}" +
               $"Created: {createdText}{Environment.NewLine}" +
               $"Description: {description}";
    }

    private static void LoadEnvironment()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var envPath = Path.Combine(directory.FullName, "src", "GmvAgent", ".env");
            if (File.Exists(envPath))
            {
                DotNetEnv.Env.Load(envPath);
                return;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find src/GmvAgent/.env from the test output directory.");
    }
}