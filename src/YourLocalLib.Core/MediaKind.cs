namespace YourLocalLib.Core;

/// <summary>
/// The five types Libib catalogs: books, movies, music (records, CDs, vinyl), video games, and board games.
/// </summary>
public static class MediaKind
{
    public const string Book = "book";
    public const string Movie = "movie";
    public const string Music = "music";
    public const string VideoGame = "videogame";
    public const string BoardGame = "boardgame";

    public static readonly string[] All = { Book, Movie, Music, VideoGame, BoardGame };

    public static string Parse(string? raw)
    {
        var text = Compact(raw);
        return text switch
        {
            "book" or "books" => Book,
            "movie" or "movies" or "film" or "films" or "dvd" or "bluray" or "bd" => Movie,
            "music" or "album" or "albums" or "record" or "records" or "cd" or "cds" or "vinyl" or "lp" or "cassette" => Music,
            "videogame" or "videogames" or "game" or "games" => VideoGame,
            "boardgame" or "boardgames" => BoardGame,
            _ => Book
        };
    }

    /// <summary>Libib's type-only CSV templates have no item_type column. The extra headers still give the type away.</summary>
    public static string FromHeaders(IEnumerable<string> headers)
    {
        var set = new HashSet<string>(headers.Select(Compact), StringComparer.Ordinal);
        if (set.Contains("ensemble") || set.Contains("aspectratio"))
            return Movie;
        if (set.Contains("esrb"))
            return VideoGame;
        if (set.Contains("numberofplayers") || set.Contains("agegroup") || set.Contains("artists"))
            return BoardGame;
        if (set.Contains("numberofdiscs"))
            return Music;
        return Book;
    }

    public static string Label(string? kind) => Parse(kind) switch
    {
        Movie => "Movie",
        Music => "Music",
        VideoGame => "Video game",
        BoardGame => "Board game",
        _ => "Book"
    };

    public static string Plural(string? kind) => Parse(kind) switch
    {
        Movie => "movies",
        Music => "albums",
        VideoGame => "video games",
        BoardGame => "board games",
        _ => "books"
    };

    public static string CreatorLabel(string? kind) => Parse(kind) switch
    {
        Movie => "Director",
        Music => "Artist",
        VideoGame => "Platform",
        BoardGame => "Designer",
        _ => "Author"
    };

    public static string DoneLabel(string? kind) => Parse(kind) switch
    {
        Movie => "Watched",
        Music => "Listened",
        VideoGame => "Played",
        BoardGame => "Played",
        _ => "Read"
    };

    public static string CodeLabel(string? kind) => Parse(kind) == Book ? "ISBN" : "UPC";

    public static bool LooksUpOnline(string? kind) => Parse(kind) is Book or Music;

    public static string Summarize(IEnumerable<KindCount> counts)
    {
        var parts = counts
            .Where(row => row.Count > 0)
            .Select(row => row.Count + " " + (row.Count == 1 ? Label(row.Kind).ToLowerInvariant() : Plural(row.Kind)))
            .ToList();
        return parts.Count == 0 ? "No titles" : string.Join(", ", parts);
    }

    private static string Compact(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? ""
            : new string(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
