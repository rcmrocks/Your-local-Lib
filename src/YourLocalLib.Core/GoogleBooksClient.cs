using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YourLocalLib.Core;

public sealed class GoogleBooksClient
{
    private readonly WebSource _web;
    private readonly Func<string?> _key;

    /// <param name="key">Reads the optional Google Books key each time, so a key saved in Settings applies at once.</param>
    public GoogleBooksClient(WebSource web, Func<string?>? key = null)
    {
        _web = web;
        _key = key ?? (() => null);
    }

    public async Task<GoogleLookup> LookupAsync(string isbn13, CancellationToken cancellationToken = default)
    {
        var url = "https://www.googleapis.com/books/v1/volumes?q=isbn:" + isbn13;
        var key = _key()?.Trim();
        if (!string.IsNullOrEmpty(key))
            url += "&key=" + Uri.EscapeDataString(key);
        var answer = await _web.GetAsync(url, cancellationToken);
        if (answer.Status != LookupStatus.Found)
            return GoogleLookup.None(answer.Status);
        try
        {
            using var document = JsonDocument.Parse(answer.Body ?? Array.Empty<byte>());
            return Parse(document, isbn13);
        }
        catch (JsonException)
        {
            return GoogleLookup.None(LookupStatus.Failed);
        }
    }

    public static GoogleLookup Parse(JsonDocument document, string isbn13)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return GoogleLookup.None(LookupStatus.Failed);
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return GoogleLookup.None(LookupStatus.NotFound);

        var isbn10 = IsbnRules.ToIsbn10(isbn13);
        JsonElement? chosen = null;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("volumeInfo", out var info)
                || info.ValueKind != JsonValueKind.Object)
                continue;
            chosen ??= info;
            if (HasIsbn(info, isbn13, isbn10))
            {
                chosen = info;
                break;
            }
        }
        if (chosen is not JsonElement volume)
            return GoogleLookup.None(LookupStatus.NotFound);

        var title = Text(volume, "title");
        var book = title == null ? null : new LookupBook(title, string.Join(", ", Strings(volume, "authors")));
        var categories = Strings(volume, "categories")
            .SelectMany(category => category.Split('/'))
            .Select(part => part.Trim())
            .Where(part => !part.Equals("General", StringComparison.OrdinalIgnoreCase));
        return new GoogleLookup(
            LookupStatus.Found,
            book,
            CleanHtml(Text(volume, "description")),
            OpenLibraryClient.ChooseSubjects(categories),
            CoverUrl(volume));
    }

    /// <summary>Google descriptions carry simple HTML. Paragraphs become blank lines and line breaks stay.</summary>
    public static string? CleanHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;
        var text = html.Replace("\r", "");
        text = Regex.Replace(text, @"<\s*/\s*(p|div)\s*>", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*br\s*/?\s*>|<\s*/\s*li\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]*>", "");
        text = WebUtility.HtmlDecode(text);
        var lines = text.Split('\n').Select(line => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        var joined = Regex.Replace(string.Join("\n", lines), @"\n{3,}", "\n\n").Trim();
        return joined.Length == 0 ? null : joined;
    }

    private static bool HasIsbn(JsonElement volume, string isbn13, string? isbn10)
    {
        if (!volume.TryGetProperty("industryIdentifiers", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var id in ids.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.Object || Text(id, "identifier") is not string value)
                continue;
            if (value == isbn13 || (isbn10 != null && string.Equals(value, isbn10, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static string? CoverUrl(JsonElement volume)
    {
        if (!volume.TryGetProperty("imageLinks", out var links) || links.ValueKind != JsonValueKind.Object)
            return null;
        var url = Text(links, "thumbnail") ?? Text(links, "smallThumbnail");
        if (url == null)
            return null;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url[7..];
        return url.Replace("&edge=curl", "", StringComparison.Ordinal);
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static IEnumerable<string> Strings(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
            .Select(value => value.GetString()!.Trim())
            .ToList();
    }
}
