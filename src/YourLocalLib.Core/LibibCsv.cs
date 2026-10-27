using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace YourLocalLib.Core;

public static class LibibCsv
{
    public static IReadOnlyList<LibibRow> ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream).Rows;
    }

    public static IReadOnlyList<LibibRow> Read(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return Load(stream).Rows;
    }

    public static IReadOnlyList<LibibRow> Read(Stream stream) => Load(stream).Rows;

    public static LibibFile LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static LibibFile Load(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            PrepareHeaderForMatch = args => args.Header.Trim().TrimStart('\uFEFF').ToLowerInvariant(),
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
            DetectColumnCountChanges = false
        };
        using var csv = new CsvReader(reader, config);
        csv.Context.RegisterClassMap<LibibRowMap>();
        if (!csv.Read() || !csv.ReadHeader())
            throw new InvalidDataException("That file has no header row.");
        var headers = csv.HeaderRecord ?? Array.Empty<string>();
        if (!headers.Any(h => string.Equals(h.Trim(), "title", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("That file does not look like a Libib export. The header needs a title column.");
        return new LibibFile(csv.GetRecords<LibibRow>().ToList(), headers);
    }

    public static IReadOnlyList<ResolvedBook> Resolve(IReadOnlyList<LibibRow> rows, string? defaultKind = null, IReadOnlyList<string>? headers = null)
    {
        var fallback = string.IsNullOrWhiteSpace(defaultKind)
            ? headers == null ? MediaKind.Book : MediaKind.FromHeaders(headers)
            : MediaKind.Parse(defaultKind);
        var resolved = rows.Select(row => ResolveRow(row, fallback)).ToList();
        var groups = new Dictionary<string, List<ResolvedBook>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var book in resolved)
        {
            if (!groups.TryGetValue(book.GroupKey, out var list))
            {
                list = new List<ResolvedBook>();
                groups[book.GroupKey] = list;
                order.Add(book.GroupKey);
            }
            list.Add(book);
        }

        var merged = new List<ResolvedBook>();
        foreach (var key in order)
        {
            var list = groups[key];
            var first = list[0];
            merged.Add(new ResolvedBook
            {
                IsBook = first.IsBook,
                Kind = first.Kind,
                CopiesInvalid = list.Any(b => b.CopiesInvalid),
                LibibId = First(list.Select(b => b.LibibId)),
                Isbn13 = first.Isbn13,
                Isbn10 = First(list.Select(b => b.Isbn10)) ?? first.Isbn10,
                Upc = First(list.Select(b => b.Upc)) ?? first.Upc,
                Title = list.OrderByDescending(b => b.Title.Length).First().Title,
                Creators = First(list.Select(b => b.Creators)),
                Collection = First(list.Select(b => b.Collection)),
                Copies = list.Sum(b => b.CopiesInvalid ? 0 : b.Copies),
                Description = list.Select(b => b.Description).Where(s => !string.IsNullOrWhiteSpace(s)).OrderByDescending(s => s!.Length).FirstOrDefault(),
                Publisher = First(list.Select(b => b.Publisher)),
                PublishDate = First(list.Select(b => b.PublishDate)),
                Series = First(list.Select(b => b.Series)),
                Tags = First(list.Select(b => b.Tags)),
                Notes = First(list.Select(b => b.Notes)),
                Rating = First(list.Select(b => b.Rating)),
                Status = First(list.Select(b => b.Status)),
                LengthOf = First(list.Select(b => b.LengthOf)),
                NumberOfDiscs = First(list.Select(b => b.NumberOfDiscs)),
                Ensemble = First(list.Select(b => b.Ensemble)),
                AspectRatio = First(list.Select(b => b.AspectRatio)),
                Esrb = First(list.Select(b => b.Esrb)),
                Artists = First(list.Select(b => b.Artists)),
                NumberOfPlayers = First(list.Select(b => b.NumberOfPlayers)),
                AgeGroup = First(list.Select(b => b.AgeGroup)),
                GroupKey = key
            });
        }
        return merged;
    }

    public static ResolvedBook ResolveRow(LibibRow row, string? defaultKind = null)
    {
        var kind = string.IsNullOrWhiteSpace(row.ItemType)
            ? MediaKind.Parse(defaultKind)
            : MediaKind.Parse(row.ItemType);
        var creators = row.Creators?.Trim();
        if (string.IsNullOrEmpty(creators))
        {
            var joined = $"{row.FirstName} {row.LastName}".Trim();
            creators = string.IsNullOrEmpty(joined) ? null : joined;
        }

        var copiesInvalid = false;
        var copies = 1;
        var copiesText = row.Copies?.Trim();
        if (!string.IsNullOrEmpty(copiesText))
        {
            if (!int.TryParse(copiesText, NumberStyles.None, CultureInfo.InvariantCulture, out copies) || copies < 0)
                copiesInvalid = true;
        }

        string? isbn13 = null;
        string? isbn10 = null;
        string? upc = null;
        var ean = IsbnRules.Parse(row.EanIsbn13);
        var upcParse = IsbnRules.Parse(row.UpcIsbn10);
        if (ean.IsCode)
        {
            isbn13 = ean.Isbn13;
            isbn10 = ean.Isbn10;
            upc = ean.Upc;
        }
        else if (upcParse.IsCode)
        {
            isbn13 = upcParse.Isbn13;
            isbn10 = upcParse.Isbn10;
            upc = upcParse.Upc;
        }
        if (upc == null && upcParse.Upc != null)
            upc = upcParse.Upc;

        var title = string.IsNullOrWhiteSpace(row.Title) ? "(Untitled)" : row.Title.Trim();
        var libibId = string.IsNullOrWhiteSpace(row.Id) ? null : row.Id.Trim();
        var collection = string.IsNullOrWhiteSpace(row.Collection) ? null : row.Collection.Trim();
        var key = kind + ":" + (isbn13 != null
            ? "isbn:" + isbn13
            : libibId != null
                ? "id:" + libibId
                : "loose:" + TextMatch.Normalize(collection) + "|" + TextMatch.Normalize(title) + "|" + TextMatch.Normalize(creators));

        return new ResolvedBook
        {
            IsBook = kind == MediaKind.Book,
            Kind = kind,
            CopiesInvalid = copiesInvalid,
            LibibId = libibId,
            Isbn13 = isbn13,
            Isbn10 = isbn10,
            Upc = upc,
            Title = title,
            Creators = creators,
            Collection = collection,
            Copies = copiesInvalid ? 0 : copies,
            Description = Blank(row.Description),
            Publisher = Blank(row.Publisher),
            PublishDate = Blank(row.PublishDate),
            Series = Blank(row.Group),
            Tags = Blank(row.Tags),
            Notes = Blank(row.Notes),
            Rating = Blank(row.Rating),
            Status = Blank(row.Status),
            LengthOf = Blank(row.LengthOf) ?? Blank(row.Length),
            NumberOfDiscs = Blank(row.NumberOfDiscs),
            Ensemble = Blank(row.Ensemble),
            AspectRatio = Blank(row.AspectRatio),
            Esrb = Blank(row.Esrb),
            Artists = Blank(row.Artists),
            NumberOfPlayers = Blank(row.NumberOfPlayers),
            AgeGroup = Blank(row.AgeGroup),
            GroupKey = key
        };
    }

    private static string? First(IEnumerable<string?> values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class LibibRowMap : ClassMap<LibibRow>
    {
        public LibibRowMap()
        {
            Map(m => m.Id).Name("id").Optional();
            Map(m => m.ItemType).Name("item_type").Optional();
            Map(m => m.Title).Name("title").Optional();
            Map(m => m.Creators).Name("creators").Optional();
            Map(m => m.FirstName).Name("first_name").Optional();
            Map(m => m.LastName).Name("last_name").Optional();
            Map(m => m.Collection).Name("collection").Optional();
            Map(m => m.EanIsbn13).Name("ean_isbn13").Optional();
            Map(m => m.UpcIsbn10).Name("upc_isbn10").Optional();
            Map(m => m.Description).Name("description").Optional();
            Map(m => m.Publisher).Name("publisher").Optional();
            Map(m => m.PublishDate).Name("publish_date").Optional();
            Map(m => m.Group).Name("group").Optional();
            Map(m => m.Tags).Name("tags").Optional();
            Map(m => m.Notes).Name("notes").Optional();
            Map(m => m.Rating).Name("rating").Optional();
            Map(m => m.Status).Name("status").Optional();
            Map(m => m.Copies).Name("copies").Optional();
            Map(m => m.LengthOf).Name("length_of").Optional();
            Map(m => m.Length).Name("length").Optional();
            Map(m => m.NumberOfDiscs).Name("number_of_discs").Optional();
            Map(m => m.Ensemble).Name("ensemble").Optional();
            Map(m => m.AspectRatio).Name("aspect_ratio").Optional();
            Map(m => m.Esrb).Name("esrb").Optional();
            Map(m => m.Artists).Name("artists").Optional();
            Map(m => m.NumberOfPlayers).Name("number_of_players").Optional();
            Map(m => m.AgeGroup).Name("age_group").Optional();
        }
    }
}
