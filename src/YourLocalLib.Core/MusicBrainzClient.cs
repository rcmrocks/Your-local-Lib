using System.Text.Json;

namespace YourLocalLib.Core;

public sealed record MusicBrainzLookup(LookupStatus Status, LookupBook? Book, string? Date, string? CoverUrl)
{
    public static MusicBrainzLookup None(LookupStatus status) => new(status, null, null, null);
}

/// <summary>Looks up a CD, vinyl, or cassette by barcode. MusicBrainz is free and needs no key.</summary>
public sealed class MusicBrainzClient
{
    private readonly WebSource _web;

    public MusicBrainzClient(WebSource web) => _web = web;

    public static string SearchUrl(string barcode) =>
        "https://musicbrainz.org/ws/2/release/?query=barcode:" + Uri.EscapeDataString(barcode) + "&fmt=json";

    public static string CoverUrl(string releaseId) =>
        "https://coverartarchive.org/release/" + releaseId + "/front-500";

    public async Task<MusicBrainzLookup> LookupAsync(string barcode, CancellationToken cancellationToken = default)
    {
        var answer = await _web.GetAsync(SearchUrl(barcode), cancellationToken);
        if (answer.Status != LookupStatus.Found)
            return MusicBrainzLookup.None(answer.Status);
        try
        {
            using var document = JsonDocument.Parse(answer.Body ?? Array.Empty<byte>());
            return Parse(document);
        }
        catch (JsonException)
        {
            return MusicBrainzLookup.None(LookupStatus.Failed);
        }
    }

    public static MusicBrainzLookup Parse(JsonDocument document)
    {
        if (!document.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
            return MusicBrainzLookup.None(LookupStatus.Failed);
        foreach (var release in releases.EnumerateArray())
        {
            var title = Text(release, "title");
            if (title == null)
                continue;
            var artists = new List<string>();
            if (release.TryGetProperty("artist-credit", out var credits) && credits.ValueKind == JsonValueKind.Array)
            {
                foreach (var credit in credits.EnumerateArray())
                {
                    if (credit.ValueKind != JsonValueKind.Object)
                        continue;
                    var name = Text(credit, "name");
                    if (name == null && credit.TryGetProperty("artist", out var artist) && artist.ValueKind == JsonValueKind.Object)
                        name = Text(artist, "name");
                    if (name != null)
                        artists.Add(name);
                }
            }
            var id = Text(release, "id");
            return new MusicBrainzLookup(
                LookupStatus.Found,
                new LookupBook(title, string.Join(", ", artists)),
                Text(release, "date"),
                id == null ? null : CoverUrl(id));
        }
        return MusicBrainzLookup.None(LookupStatus.NotFound);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
