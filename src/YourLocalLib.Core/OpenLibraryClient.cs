using System.Text.Json;
using System.Text.RegularExpressions;

namespace YourLocalLib.Core;

public sealed class OpenLibraryClient
{
    private static readonly Regex WorkKey = new(@"^/works/OL\d+W$", RegexOptions.CultureInvariant);
    private readonly WebSource _web;

    public OpenLibraryClient(WebSource web) => _web = web;

    public static string IsbnCoverUrl(string isbn13) =>
        "https://covers.openlibrary.org/b/isbn/" + isbn13 + "-L.jpg?default=false";

    public static string CoverUrlById(long id) =>
        "https://covers.openlibrary.org/b/id/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-L.jpg";

    public async Task<OpenLibraryLookup> LookupAsync(string isbn13, CancellationToken cancellationToken = default)
    {
        var answer = await _web.GetAsync("https://openlibrary.org/api/books?bibkeys=ISBN:" + isbn13 + "&format=json&jscmd=data", cancellationToken);
        if (answer.Status != LookupStatus.Found)
            return OpenLibraryLookup.None(answer.Status);
        try
        {
            using var document = JsonDocument.Parse(answer.Body ?? Array.Empty<byte>());
            return Parse(document);
        }
        catch (JsonException)
        {
            return OpenLibraryLookup.None(LookupStatus.Failed);
        }
    }

    public static OpenLibraryLookup Parse(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return OpenLibraryLookup.None(LookupStatus.Failed);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var item = property.Value;
            if (item.ValueKind != JsonValueKind.Object)
                return OpenLibraryLookup.None(LookupStatus.Failed);
            var title = Text(item, "title");
            var authors = new List<string>();
            if (item.TryGetProperty("authors", out var authorEl) && authorEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var author in authorEl.EnumerateArray())
                {
                    if (author.ValueKind == JsonValueKind.Object && Text(author, "name") is string name)
                        authors.Add(name);
                }
            }
            string? cover = null;
            if (item.TryGetProperty("cover", out var coverEl) && coverEl.ValueKind == JsonValueKind.Object)
                cover = Text(coverEl, "large") ?? Text(coverEl, "medium");
            var book = title == null ? null : new LookupBook(title, string.Join(", ", authors));
            return new OpenLibraryLookup(LookupStatus.Found, book, ChooseSubjects(ReadSubjectNames(item)), cover);
        }
        return OpenLibraryLookup.None(LookupStatus.NotFound);
    }

    /// <summary>Reads the edition record, and the work record when the edition has no description.</summary>
    public async Task<OpenLibraryDetails> DetailsAsync(string isbn13, CancellationToken cancellationToken = default)
    {
        var edition = await _web.GetAsync("https://openlibrary.org/isbn/" + isbn13 + ".json", cancellationToken);
        if (edition.Status != LookupStatus.Found)
            return new OpenLibraryDetails(edition.Status, null, Array.Empty<long>());
        if (ReadRecord(edition.Body) is not RecordParts record)
            return new OpenLibraryDetails(LookupStatus.Failed, null, Array.Empty<long>());
        if (record.Description != null || record.Work == null)
            return new OpenLibraryDetails(LookupStatus.Found, record.Description, record.Covers);

        var work = await _web.GetAsync("https://openlibrary.org" + record.Work + ".json", cancellationToken);
        var workRecord = work.Status == LookupStatus.Found ? ReadRecord(work.Body) : null;
        var covers = record.Covers.Concat(workRecord?.Covers ?? Array.Empty<long>()).Distinct().Take(3).ToList();
        var status = work.Status switch
        {
            LookupStatus.Found => workRecord != null ? LookupStatus.Found : LookupStatus.Failed,
            LookupStatus.NotFound => LookupStatus.Found,
            _ => work.Status
        };
        return new OpenLibraryDetails(status, workRecord?.Description, covers);
    }

    public static IReadOnlyList<string> ChooseSubjects(IEnumerable<string?>? raw)
    {
        if (raw == null)
            return Array.Empty<string>();
        var prepared = new List<(string Display, int Index)>();
        var index = 0;
        foreach (var item in raw)
        {
            var current = index++;
            if (string.IsNullOrWhiteSpace(item))
                continue;
            var display = string.Join(' ', item.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (display.Length == 0 || display.Length > 32)
                continue;
            if (IsYearOrNumber(display))
                continue;
            prepared.Add((display, current));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var chosen = new List<string>();
        foreach (var item in prepared.OrderBy(item => item.Display.Length).ThenBy(item => item.Index))
        {
            var key = TextMatch.Normalize(item.Display);
            if (key.Length == 0 || !seen.Add(key))
                continue;
            chosen.Add(item.Display);
            if (chosen.Count == 8)
                break;
        }
        return chosen;
    }

    private sealed record RecordParts(string? Description, string? Work, IReadOnlyList<long> Covers);

    private static RecordParts? ReadRecord(byte[]? body)
    {
        if (body == null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            return new RecordParts(ReadDescription(root), ReadWorkKey(root), ReadCovers(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadDescription(JsonElement record)
    {
        if (!record.TryGetProperty("description", out var description))
            return null;
        var text = description.ValueKind switch
        {
            JsonValueKind.String => description.GetString(),
            JsonValueKind.Object => Text(description, "value"),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Replace("\r\n", "\n").Trim();
    }

    private static string? ReadWorkKey(JsonElement record)
    {
        if (!record.TryGetProperty("works", out var works) || works.ValueKind != JsonValueKind.Array)
            return null;
        var first = works.EnumerateArray().FirstOrDefault();
        var key = first.ValueKind == JsonValueKind.Object ? Text(first, "key") : null;
        return key != null && WorkKey.IsMatch(key) ? key : null;
    }

    private static IReadOnlyList<long> ReadCovers(JsonElement record)
    {
        if (!record.TryGetProperty("covers", out var covers) || covers.ValueKind != JsonValueKind.Array)
            return Array.Empty<long>();
        var ids = new List<long>();
        foreach (var cover in covers.EnumerateArray())
        {
            // Open Library marks a removed cover with -1.
            if (cover.ValueKind == JsonValueKind.Number && cover.TryGetInt64(out var id) && id > 0 && !ids.Contains(id))
                ids.Add(id);
            if (ids.Count == 3)
                break;
        }
        return ids;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static bool IsYearOrNumber(string value)
    {
        if (value.All(char.IsDigit))
            return true;
        var rest = value.AsSpan().Trim();
        if (rest.Length == 0 || rest[0] is not ('c' or 'C'))
            return false;
        rest = rest[1..];
        if (rest.Length > 0 && rest[0] == '.')
            rest = rest[1..];
        rest = rest.TrimStart();
        return rest.Length is 3 or 4 && rest.ToString().All(char.IsDigit);
    }

    private static List<string> ReadSubjectNames(JsonElement item)
    {
        var names = new List<string>();
        if (!item.TryGetProperty("subjects", out var subjects) || subjects.ValueKind != JsonValueKind.Array)
            return names;
        foreach (var subject in subjects.EnumerateArray())
        {
            if (subject.ValueKind == JsonValueKind.String)
            {
                if (subject.GetString() is string text)
                    names.Add(text);
            }
            else if (subject.ValueKind == JsonValueKind.Object && Text(subject, "name") is string value)
            {
                names.Add(value);
            }
        }
        return names;
    }
}
