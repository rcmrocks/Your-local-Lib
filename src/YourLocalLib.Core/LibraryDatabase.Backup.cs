using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace YourLocalLib.Core;

public sealed partial class LibraryDatabase
{
    internal const int SchemaVersion = 6;

    // A table comes after every table it points at.
    private static readonly string[] LibraryTables =
    {
        "settings", "locations", "retired_location_codes", "titles", "title_isbns", "title_collections",
        "title_tags", "copies", "sessions", "scans", "pending_actions"
    };

    private long _generation;

    /// <summary>Goes up each time the whole library is replaced, so work begun before then can tell its book and scan ids no longer apply.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>Writes a consistent copy of the whole database to <paramref name="path"/> as one self-contained file.</summary>
    internal void SnapshotTo(string path) => Locked(() =>
    {
        using var dest = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        dest.Open();
        _conn.BackupDatabase(dest);
        using var cmd = dest.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode = DELETE;";
        cmd.ExecuteNonQuery();
    });

    /// <summary>Opens a copy that is not the live library, upgrading it if it is older. The file is free again after Dispose.</summary>
    internal static LibraryDatabase OpenLoose(string path) =>
        OpenWith(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false },
            new SystemClock(), "DELETE");

    public bool HasOwnBooksOrShelves() => Locked(() =>
        ScalarLong("SELECT EXISTS(SELECT 1 FROM titles WHERE is_sample = 0) OR EXISTS(SELECT 1 FROM locations WHERE is_sample = 0)") == 1);

    internal LibraryCounts Counts() => Locked(() => new LibraryCounts(
        (int)ScalarLong("SELECT COUNT(*) FROM titles"),
        (int)ScalarLong("SELECT COUNT(*) FROM copies"),
        (int)ScalarLong("SELECT COUNT(*) FROM locations"),
        (int)ScalarLong("SELECT COUNT(*) FROM title_tags"),
        (int)ScalarLong("SELECT COUNT(*) FROM titles WHERE user_rating IS NOT NULL"),
        (int)ScalarLong("SELECT COUNT(*) FROM titles WHERE user_read = 1")));

    /// <summary>Every cover file the library points at: on books, on scan cards, and in undo records.</summary>
    internal IReadOnlyList<string> CoverReferences() => Locked(() =>
    {
        var paths = QueryStrings("""
            SELECT cover_path FROM titles WHERE trim(IFNULL(cover_path, '')) <> ''
            UNION
            SELECT cover_path FROM scans WHERE trim(IFNULL(cover_path, '')) <> ''
            """);
        paths.AddRange(UndoCovers().Select(row => row.Snapshot.ProvisionalCover!));
        return paths.Select(path => path.Trim()).Distinct(StringComparer.Ordinal).ToList();
    });

    /// <summary>
    /// Replaces every row with the rows of the library at <paramref name="sourcePath"/> in one transaction, so a failure changes nothing.
    /// <paramref name="cover"/> gives the new place of each cover path in that library, or null when the file is gone.
    /// </summary>
    internal void ReplaceWith(string sourcePath, Func<string, string?> cover) => Locked(() =>
    {
        Exec("ATTACH DATABASE $path AS restored", ("$path", sourcePath));
        try
        {
            using var tx = _conn.BeginTransaction();
            Exec("PRAGMA defer_foreign_keys = ON;");
            foreach (var table in LibraryTables.Reverse())
                Exec("DELETE FROM main." + table);
            foreach (var table in LibraryTables)
            {
                var source = Columns("restored", table).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var columns = string.Join(", ", Columns("main", table).Where(source.Contains).Select(name => "\"" + name + "\""));
                if (columns.Length > 0)
                    Exec("INSERT INTO main." + table + " (" + columns + ") SELECT " + columns + " FROM restored." + table);
            }
            RepointCovers(cover);
            tx.Commit();
        }
        finally
        {
            Exec("DETACH DATABASE restored");
        }
        _lastLiveIsbn = null;
        Interlocked.Increment(ref _generation);
    });

    private void RepointCovers(Func<string, string?> cover)
    {
        foreach (var table in new[] { "titles", "scans" })
        {
            var rows = Query("SELECT id, cover_path FROM " + table + " WHERE cover_path IS NOT NULL",
                r => (Id: r.GetInt64(0), Path: r.GetString(1)));
            foreach (var row in rows)
            {
                var now = string.IsNullOrWhiteSpace(row.Path) ? null : cover(row.Path);
                if (now != row.Path)
                    Exec("UPDATE " + table + " SET cover_path = $p WHERE id = $id", ("$p", now), ("$id", row.Id));
            }
        }
        foreach (var (id, snapshot) in UndoCovers())
        {
            var now = cover(snapshot.ProvisionalCover!);
            if (now == snapshot.ProvisionalCover)
                continue;
            snapshot.ProvisionalCover = now;
            Exec("UPDATE scans SET undo_payload = $p WHERE id = $id", ("$p", Json(snapshot)), ("$id", id));
        }
    }

    private List<(long Id, UndoSnapshot Snapshot)> UndoCovers()
    {
        var rows = Query("SELECT id, undo_payload FROM scans WHERE undo_payload LIKE '%ProvisionalCover%'",
            r => (Id: r.GetInt64(0), Json: r.GetString(1)));
        var list = new List<(long, UndoSnapshot)>();
        foreach (var row in rows)
        {
            UndoSnapshot? snapshot;
            try
            {
                snapshot = ReadJson(row.Json);
            }
            catch (JsonException)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(snapshot?.ProvisionalCover))
                list.Add((row.Id, snapshot));
        }
        return list;
    }

    private List<string> Columns(string schema, string table) =>
        Query("PRAGMA " + schema + ".table_info(" + table + ")", r => r.GetString(1));
}
