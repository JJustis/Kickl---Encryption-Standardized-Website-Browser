using Kickl.Core.Markup;

namespace Kickl.Core.Registry;

/// <summary>One live-index hit: the public metadata of an active site.</summary>
public sealed class SiteSummary
{
    public string Host { get; set; } = "";
    public string Title { get; set; } = "";
    public string Headline { get; set; } = "";
    public long UpdatedUtc { get; set; }
    public string Author { get; set; } = "";
    public string StoryDate { get; set; } = "";
    public string Location { get; set; } = "";
    public string[] Tags { get; set; } = [];
    public string Status { get; set; } = "active";
    public string OwnerId { get; set; } = "";
    public string BadgeId { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public int Version { get; set; }
}

public sealed class SearchResult
{
    public string Query { get; set; } = "";
    public List<SiteSummary> Results { get; set; } = [];
}

/// <summary>
/// The phonebook query engine. A search is by required tag, or by substring of the name,
/// title, or headline. Closed names never appear. This is the same data the live index at
/// secupgrade.com/legenddxI2 serves, and it travels over the PFS channel, not HTTP.
/// </summary>
public static class Phonebook
{
    public const int MaxResults = 50;

    public static SiteSummary SummaryOf(RegistryRecord record) => new()
    {
        Host = record.Name,
        Title = string.IsNullOrWhiteSpace(record.Title) ? record.Name : record.Title,
        Headline = record.Headline,
        UpdatedUtc = record.UpdatedUtc,
        Author = record.StoryAuthor,
        StoryDate = record.StoryDate,
        Location = record.StoryLocation,
        Tags = record.Tags,
        Status = record.Status,
        OwnerId = record.OwnerId,
        BadgeId = record.BadgeId,
        ContentHash = record.ContentHash,
        Version = record.Version
    };

    public static SearchResult Search(RegistryFile file, string query)
    {
        var q = (query ?? "").Trim().ToLowerInvariant();
        var result = new SearchResult { Query = q };
        if (q.Length == 0) return result;

        result.Results = file.Records
            .Where(r => !LocalRegistry.IsClosed(r))
            .Where(r => Matches(r, q))
            .OrderByDescending(r => r.Tags.Any(t => string.Equals(t, q, StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(r => r.UpdatedUtc)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .Take(MaxResults)
            .Select(SummaryOf)
            .ToList();

        return result;
    }

    private static bool Matches(RegistryRecord record, string q) =>
        record.Tags.Any(t => string.Equals(t, q, StringComparison.OrdinalIgnoreCase))
        || record.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
        || record.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
        || record.Headline.Contains(q, StringComparison.OrdinalIgnoreCase);

    /// <summary>Publish-time contract: every searchable site needs a headline and 1–8 valid tags.</summary>
    public static void ValidateMetadata(string headline, IReadOnlyList<string> tags)
    {
        if (string.IsNullOrWhiteSpace(headline))
            throw new KmlException("Publishing requires <meta name=\"headline\" content=\"...\"/>.");
        if (tags.Count == 0)
            throw new KmlException("Publishing requires at least one tag in <meta name=\"tags\" content=\"a, b\"/>.");
        if (tags.Count > Kml.MaxTags)
            throw new KmlException($"A site may declare at most {Kml.MaxTags} tags.");
        foreach (var tag in tags)
        {
            if (!Kml.IsValidTag(tag))
                throw new KmlException($"Invalid tag '{tag}': use 2–24 lowercase letters, digits, or hyphens.");
        }
    }
}
