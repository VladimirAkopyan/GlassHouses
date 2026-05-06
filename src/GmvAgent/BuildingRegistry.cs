using System.Globalization;
using System.Text.RegularExpressions;

// Canonical registry of GMV buildings. Loaded once at startup from
// gmv_buildings_postcodes.csv (which lives next to this project's exe).
//
// Used to:
//   - Detect when a user's question mentions a specific named building so the
//     chatbot can scope the vector search to that building's chunks.
//   - Provide a human-readable list of the 40 buildings + 3 phase-6 sub-buildings.
//
// Mirrors the Python building_registry.py used at ingestion — keep in sync if you
// change one. Both share the same source-of-truth CSV file.
public sealed class BuildingRegistry
{
    public sealed record Building(
        string Side,
        int Number,
        string Name,
        string Slug,
        string Street,
        string Postcode);

    public IReadOnlyList<Building> All { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<Building>> ByPostcode { get; }

    // Variants the user might type that we should still recognise.
    private static readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Nasmythe House", "Nasmyth House" },          // register.md spelling
        { "Da Vinci Lodge", "Da Vinci Lodge" },         // canonical (no-op, kept for explicitness)
        { "DaVinci Lodge", "Da Vinci Lodge" },          // common typo
    };

    // Phase-6 acoustic-report buildings — referenced as "Building 302/303/304" but
    // not in the canonical 40-building list. We treat them as recognisable entities.
    private static readonly Building[] _phase6 = new[]
    {
        new Building("Phase6", 302, "Building 302", "building_302", "", ""),
        new Building("Phase6", 303, "Building 303", "building_303", "", ""),
        new Building("Phase6", 304, "Building 304", "building_304", "", ""),
    };

    private readonly Regex _detectRegex;
    private readonly Dictionary<string, string> _matchedToCanonical;

    public BuildingRegistry()
    {
        var csvPath = LocateCsv()
            ?? throw new InvalidOperationException("gmv_buildings_postcodes.csv not found next to GmvAgent.dll or in cwd.");

        var rows = new List<Building>();
        var byPostcode = new Dictionary<string, List<Building>>();
        foreach (var line in File.ReadLines(csvPath).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length < 5) continue;
            var b = new Building(
                Side: parts[0].Trim(),
                Number: int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
                Name: parts[2].Trim(),
                Slug: Slugify(parts[2].Trim()),
                Street: parts[3].Trim(),
                Postcode: parts[4].Trim());
            rows.Add(b);
            if (!byPostcode.TryGetValue(b.Postcode, out var list))
            {
                list = new List<Building>(); byPostcode[b.Postcode] = list;
            }
            list.Add(b);
        }
        rows.AddRange(_phase6);

        All = rows;
        ByPostcode = byPostcode.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<Building>)kv.Value);

        // Build a single regex that matches any canonical name OR alias as a whole word.
        // Sort by length DESC so "New Becquerel Court" wins over "Becquerel Court" when both
        // would otherwise match.
        _matchedToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in All) _matchedToCanonical[b.Name] = b.Name;
        foreach (var kv in _aliases) _matchedToCanonical[kv.Key] = kv.Value;

        var ordered = _matchedToCanonical.Keys
            .OrderByDescending(k => k.Length)
            .Select(Regex.Escape);
        var pattern = $@"\b(?:{string.Join("|", ordered)})\b";
        _detectRegex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// Returns the canonical building names mentioned in `text`. Order is deterministic
    /// (alphabetical). Empty list if none found.
    public IReadOnlyList<string> Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in _detectRegex.Matches(text))
        {
            if (_matchedToCanonical.TryGetValue(m.Value, out var canonical))
            {
                found.Add(canonical);
            }
        }
        return found.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    public static string Slugify(string name)
    {
        var lowered = name.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch == ' ' || ch == '-') sb.Append('_');
            // drop everything else
        }
        return sb.ToString();
    }

    private static string? LocateCsv()
    {
        // Search next to the assembly first (Docker/published), then walk up from cwd
        // (dotnet run / dev).
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "gmv_buildings_postcodes.csv"),
            Path.Combine(Directory.GetCurrentDirectory(), "gmv_buildings_postcodes.csv"),
        };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            candidates.Add(Path.Combine(dir.FullName, "gmv_buildings_postcodes.csv"));
            dir = dir.Parent;
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}
