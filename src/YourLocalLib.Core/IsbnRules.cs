namespace YourLocalLib.Core;

public sealed record IsbnParse(bool IsIsbn, bool IsShelf, string? Isbn13, string? Isbn10, string? ShelfCode, string Message, string? Upc = null)
{
    public static IsbnParse Book(string isbn13, string? isbn10) => new(true, false, isbn13, isbn10, null, "");
    public static IsbnParse Product(string ean13, string? upc) => new(false, false, ean13, null, null, "", upc);
    public static IsbnParse Shelf(string code) => new(false, true, null, null, code, "");
    public static IsbnParse NotIsbn(string message) => new(false, false, null, null, null, message);

    /// <summary>An ISBN, UPC-A, or EAN-13 that can identify a title.</summary>
    public bool IsCode => Isbn13 != null && !IsShelf;
}

public static class IsbnRules
{
    public static IsbnParse Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return IsbnParse.NotIsbn("Not an ISBN or UPC.");

        var text = raw.Trim().Trim('\uFEFF').Replace("\r", "").Replace("\n", "");
        var shelfCandidate = text.Replace(" ", "");
        if (LocationCodes.TryParse(shelfCandidate, out var shelf))
            return IsbnParse.Shelf(shelf);

        var body = DigitsAndX(StripScannerPrefix(text));
        if (body.Length == 18 && body.All(char.IsDigit))
            body = body[..13];

        if (body.Length == 13 && body.All(char.IsDigit) && IsValidIsbn13(body))
        {
            if (body.StartsWith("978", StringComparison.Ordinal) || body.StartsWith("979", StringComparison.Ordinal))
                return IsbnParse.Book(body, ToIsbn10(body));
            return IsbnParse.Product(body, body[0] == '0' ? body[1..] : null);
        }

        if (body.Length == 12 && body.All(char.IsDigit) && IsValidUpcA(body))
            return IsbnParse.Product("0" + body, body);

        if (body.Length == 10)
        {
            var isbn10 = body[..9] + char.ToUpperInvariant(body[9]);
            if (!IsValidIsbn10(isbn10))
                return IsbnParse.NotIsbn("Not an ISBN or UPC.");
            return IsbnParse.Book(ToIsbn13(isbn10), isbn10);
        }

        return IsbnParse.NotIsbn("Not an ISBN or UPC.");
    }

    public static bool IsValidUpcA(string digits)
    {
        if (digits.Length != 12 || !digits.All(char.IsDigit))
            return false;
        var sum = 0;
        for (var i = 0; i < 11; i++)
            sum += (digits[i] - '0') * (i % 2 == 0 ? 3 : 1);
        var check = (10 - (sum % 10)) % 10;
        return check == digits[11] - '0';
    }

    public static bool IsValidIsbn13(string digits)
    {
        if (digits.Length != 13 || !digits.All(char.IsDigit))
            return false;
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (digits[i] - '0') * (i % 2 == 0 ? 1 : 3);
        var check = (10 - (sum % 10)) % 10;
        return check == digits[12] - '0';
    }

    public static bool IsValidIsbn10(string isbn10)
    {
        if (isbn10.Length != 10)
            return false;
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            if (!char.IsDigit(isbn10[i]))
                return false;
            sum += (isbn10[i] - '0') * (10 - i);
        }
        var last = isbn10[9];
        var checkValue = last is 'X' or 'x' ? 10 : char.IsDigit(last) ? last - '0' : -1;
        if (checkValue < 0)
            return false;
        return (sum + checkValue) % 11 == 0;
    }

    public static string ToIsbn13(string isbn10)
    {
        var core = "978" + isbn10[..9];
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (core[i] - '0') * (i % 2 == 0 ? 1 : 3);
        var check = (10 - (sum % 10)) % 10;
        return core + check.ToString();
    }

    public static string? ToIsbn10(string isbn13)
    {
        if (!isbn13.StartsWith("978", StringComparison.Ordinal) || isbn13.Length != 13)
            return null;
        var core = isbn13.Substring(3, 9);
        var sum = 0;
        for (var i = 0; i < 9; i++)
            sum += (core[i] - '0') * (10 - i);
        var check = (11 - (sum % 11)) % 11;
        return core + (check == 10 ? "X" : check.ToString());
    }

    private static string StripScannerPrefix(string text)
    {
        var t = text.Trim();
        if (t.Length > 3 && t[0] == ']' && char.IsLetter(t[1]) && char.IsDigit(t[2]))
            t = t[3..];
        return t;
    }

    private static string DigitsAndX(string text)
    {
        Span<char> buffer = stackalloc char[text.Length];
        var n = 0;
        foreach (var c in text)
        {
            if (char.IsDigit(c) || c is 'X' or 'x')
                buffer[n++] = c;
        }
        return new string(buffer[..n]);
    }
}

public static class LocationCodes
{
    public static bool TryParse(string compact, out string code)
    {
        code = "";
        if (compact.Length < 5 || !compact.StartsWith("LOC-", StringComparison.OrdinalIgnoreCase))
            return false;
        var digits = compact[4..];
        if (digits.Length < 4 || !digits.All(char.IsDigit))
            return false;
        code = "LOC-" + digits;
        return true;
    }
}

public static class TextMatch
{
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "translator", "translated", "editor", "illustrated", "illustrator"
    };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var chars = value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static bool SameTitle(string? a, string? b) =>
        Normalize(a).Length > 0 && Normalize(a) == Normalize(b);

    public static bool AuthorsOverlap(string? a, string? b)
    {
        var left = Tokens(a);
        var right = Tokens(b);
        if (left.Count == 0 || right.Count == 0)
            return false;
        return left.Overlaps(right);
    }

    public static HashSet<string> Tokens(string? creators)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in Normalize(creators).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 3 && !Stop.Contains(token))
                set.Add(token);
        }
        return set;
    }
}
