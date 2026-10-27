using System.Globalization;
using System.Text;
using CsvHelper;

namespace YourLocalLib.Core;

public static class CsvExports
{
    public static void WriteAddToLibib(string path, IReadOnlyList<GapRow> rows)
    {
        Write(path, rows.Select(row => new
        {
            title = row.Title,
            creators = row.Creators,
            ean_isbn13 = row.Isbn13,
            upc_isbn10 = row.Isbn13 == null ? null : IsbnRules.ToIsbn10(row.Isbn13),
            copies = 1
        }));
    }

    public static void WriteCatalog(string path, IReadOnlyList<CatalogRow> rows)
    {
        Write(path, rows.Select(row => new
        {
            title = row.Title,
            creators = row.Creators,
            item_type = row.Kind,
            ean_isbn13 = row.Isbn13,
            upc_isbn10 = row.Upc,
            series = row.Series,
            shelf = row.ShelfPath,
            last_confirmed = row.LastConfirmedUtc,
            collection = row.Collections,
            in_libib = row.InLibib ? "yes" : "no",
            read = row.UserRead ? "yes" : "no",
            user_rating = row.ShownRating is double rating ? UserRatingScale.Label(rating) : "",
            tags = row.UserTags
        }));
    }

    public static void WriteGaps(string path, IReadOnlyList<GapRow> rows)
    {
        Write(path, rows.Select(row => new
        {
            title = row.Title,
            creators = row.Creators,
            ean_isbn13 = row.Isbn13
        }));
    }

    private static void Write<T>(string path, IEnumerable<T> rows)
    {
        using var stream = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        using var csv = new CsvWriter(stream, CultureInfo.InvariantCulture);
        csv.WriteRecords(rows);
    }
}
