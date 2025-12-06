using System.Xml.Linq;

namespace MeTubeServer.Services;

public class AtomFeedParser
{
    private readonly ILogger<AtomFeedParser> _logger;

    public AtomFeedParser(ILogger<AtomFeedParser> logger)
    {
        _logger = logger;
    }

    public List<AtomEntry> ParseFeed(string atomXml)
    {
        var entries = new List<AtomEntry>();

        try
        {
            var doc = XDocument.Parse(atomXml);
            var ns = XNamespace.Get("http://www.w3.org/2005/Atom");
            var ytNs = XNamespace.Get("http://www.youtube.com/xml/schemas/2015");

            var entryElements = doc.Descendants(ns + "entry");

            foreach (var entry in entryElements)
            {
                var videoId = entry.Element(ytNs + "videoId")?.Value;
                var channelId = entry.Element(ytNs + "channelId")?.Value;
                var publishedStr = entry.Element(ns + "published")?.Value;
                var title = entry.Element(ns + "title")?.Value;

                if (string.IsNullOrEmpty(videoId) || string.IsNullOrEmpty(channelId))
                {
                    _logger.LogWarning("Skipping entry with missing videoId or channelId");
                    continue;
                }

                DateTimeOffset published = DateTimeOffset.UtcNow;
                if (!string.IsNullOrEmpty(publishedStr))
                {
                    if (!DateTimeOffset.TryParse(publishedStr, out published))
                    {
                        _logger.LogWarning("Could not parse published date: {PublishedStr}", publishedStr);
                        published = DateTimeOffset.UtcNow;
                    }
                }

                entries.Add(new AtomEntry
                {
                    VideoId = videoId,
                    ChannelId = channelId,
                    PublishedAt = published,
                    Title = title
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing Atom feed");
        }

        return entries;
    }
}

public class AtomEntry
{
    public string VideoId { get; set; } = null!;
    public string ChannelId { get; set; } = null!;
    public DateTimeOffset PublishedAt { get; set; }
    public string? Title { get; set; }
}
