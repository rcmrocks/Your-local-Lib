using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace YourLocalLib.Core;

/// <summary>A reason a backup cannot be saved or restored, worded for the person using the app.</summary>
public sealed class BackupException : Exception
{
    public BackupException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// A backup is one zip file holding library.db, every cover the library points at, and backup.json.
/// Restore also takes a library-*.db file from the automatic backups, which has no covers.
/// </summary>
public static class LibraryBackup
{
    private const string DatabaseEntry = "library.db";
    private const string ManifestEntry = "backup.json";
    private const string CoversPrefix = "covers/";
    private const int Format = 1;
    private const long MaxCoverBytes = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Writes the backup beside <paramref name="zipPath"/> first and reads it back, so a full or unplugged drive never leaves half a file under that name.</summary>
    public static BackupReport Save(LibraryDatabase db, string zipPath, string? appVersion = null)
    {
        var work = WorkFolder();
        var part = zipPath + ".part";
        try
        {
            var snapshot = Path.Combine(work, DatabaseEntry);
            db.SnapshotTo(snapshot);
            LibraryCounts counts;
            IReadOnlyList<string> references;
            using (var copy = LibraryDatabase.OpenLoose(snapshot))
            {
                counts = copy.Counts();
                references = copy.CoverReferences();
            }

            var manifest = new BackupManifest
            {
                AppVersion = appVersion,
                Schema = LibraryDatabase.SchemaVersion,
                CreatedUtc = db.Clock.UtcNow
            };
            var folder = Path.GetDirectoryName(Path.GetFullPath(zipPath));
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            using (var stream = new FileStream(part, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    zip.CreateEntryFromFile(snapshot, DatabaseEntry, CompressionLevel.Optimal);
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var path in references)
                    {
                        if (ReadCover(path) is not byte[] bytes)
                            continue;
                        var entry = CoversPrefix + EntryName(FileNameOf(path), used);
                        using (var output = zip.CreateEntry(entry, CompressionLevel.NoCompression).Open())
                            output.Write(bytes);
                        manifest.Covers[path] = entry;
                    }
                    using (var output = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal).Open())
                        JsonSerializer.Serialize(output, manifest, JsonOptions);
                }
                stream.Flush(flushToDisk: true);
            }
            Verify(part, snapshot, manifest.Covers.Count);
            File.Move(part, zipPath, overwrite: true);
            return new BackupReport(zipPath, counts, manifest.Covers.Count, new FileInfo(zipPath).Length);
        }
        catch
        {
            TryDelete(part);
            throw;
        }
        finally
        {
            TryDeleteFolder(work);
        }
    }

    /// <summary>Reads a backup without changing anything.</summary>
    /// <param name="openLibrary">The database the app has open, which is refused as a backup.</param>
    /// <exception cref="BackupException">The file cannot be restored.</exception>
    public static BackupCheck Inspect(string path, string? openLibrary = null)
    {
        using var prepared = Prepare(path, openLibrary);
        return prepared.Check;
    }

    /// <summary>
    /// Replaces the whole library with the backup at <paramref name="path"/>. A library with books or shelves of its own is
    /// saved first in <paramref name="safetyFolder"/>, and nothing is replaced when that fails or the backup is unreadable.
    /// </summary>
    /// <exception cref="BackupException">The file cannot be restored, or the current library could not be saved first.</exception>
    public static RestoreReport Restore(LibraryDatabase db, string path, string coversFolder, string safetyFolder, string? appVersion = null)
    {
        using var prepared = Prepare(path, db.Path);
        string? safety = null;
        if (db.HasOwnBooksOrShelves())
        {
            try
            {
                Directory.CreateDirectory(safetyFolder);
                var stem = "before-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                safety = Save(db, FreePath(safetyFolder, stem, ".zip"), appVersion).Path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or BackupException)
            {
                throw new BackupException("The library you have now could not be saved first, so nothing was replaced. " + ex.Message, ex);
            }
        }

        Dictionary<string, string> restored;
        try
        {
            restored = prepared.Zip == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : ExtractCovers(prepared.Zip, coversFolder);
        }
        catch (InvalidDataException ex)
        {
            throw Damaged(ex);
        }
        db.ReplaceWith(prepared.Database, CoverMap(prepared.Manifest, restored));
        return new RestoreReport(prepared.Check.Counts, restored.Count, safety);
    }

    private static Prepared Prepare(string path, string? openLibrary)
    {
        if (openLibrary != null && SamePath(path, openLibrary))
            throw new BackupException("That is the library this app has open. Choose a backup file instead.");
        var kind = KindOf(path);
        if (kind == FileKind.Other)
            throw NotABackup();

        var work = WorkFolder();
        ZipArchive? zip = null;
        try
        {
            var database = Path.Combine(work, DatabaseEntry);
            BackupManifest? manifest = null;
            DateTime? created;
            var covers = 0;
            if (kind == FileKind.Zip)
            {
                try
                {
                    zip = ZipFile.OpenRead(path);
                }
                catch (InvalidDataException ex)
                {
                    throw Damaged(ex);
                }
                var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, DatabaseEntry, StringComparison.OrdinalIgnoreCase))
                    ?? throw NotABackup();
                manifest = ReadManifest(zip);
                if (manifest?.Format > Format)
                    throw Newer();
                try
                {
                    entry.ExtractToFile(database);
                }
                catch (InvalidDataException ex)
                {
                    throw Damaged(ex);
                }
                covers = zip.Entries.Count(e => CoverName(e) != null);
                created = manifest?.CreatedUtc ?? entry.LastWriteTime.UtcDateTime;
            }
            else
            {
                File.Copy(path, database);
                created = File.GetLastWriteTimeUtc(path);
            }

            Check(database);
            LibraryCounts counts;
            using (var copy = LibraryDatabase.OpenLoose(database))
                counts = copy.Counts();
            var check = new BackupCheck(created, manifest?.AppVersion, counts, covers, kind == FileKind.Database);
            return new Prepared(work, database, zip, manifest, check);
        }
        catch
        {
            zip?.Dispose();
            TryDeleteFolder(work);
            throw;
        }
    }

    /// <summary>Refuses a newer, foreign, or damaged file before anything opens it as a library, because opening one adds the tables.</summary>
    private static void Check(string database)
    {
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            conn.Open();
            if (Scalar(conn, "PRAGMA user_version") is long version && version > LibraryDatabase.SchemaVersion)
                throw Newer();
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    tables.Add(reader.GetString(0));
            }
            if (!new[] { "titles", "copies", "locations" }.All(tables.Contains))
                throw NotABackup();
            if (!string.Equals(Scalar(conn, "PRAGMA quick_check") as string, "ok", StringComparison.OrdinalIgnoreCase))
                throw Damaged();
        }
        catch (SqliteException ex)
        {
            throw Damaged(ex);
        }
    }

    /// <summary>Copies each cover into the covers folder. A file already there is used only when it holds the same bytes.</summary>
    private static Dictionary<string, string> ExtractCovers(ZipArchive zip, string coversFolder)
    {
        var restored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (CoverName(entry) is not string name)
                continue;
            Directory.CreateDirectory(coversFolder);
            var bytes = ReadEntry(entry);
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            var target = Path.Combine(coversFolder, name);
            for (var n = 1; File.Exists(target) && !SameBytes(target, bytes); n++)
                target = Path.Combine(coversFolder, stem + "-restored" + (n == 1 ? "" : "-" + n) + extension);
            if (!File.Exists(target))
            {
                var part = target + ".part";
                File.WriteAllBytes(part, bytes);
                File.Move(part, target, overwrite: true);
            }
            restored[entry.FullName] = target;
        }
        return restored;
    }

    /// <summary>Cover paths are whole paths on the computer that made the backup, so each one is pointed at its restored file.</summary>
    private static Func<string, string?> CoverMap(BackupManifest? manifest, Dictionary<string, string> restored)
    {
        var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (old, entry) in manifest?.Covers ?? new Dictionary<string, string>())
        {
            if (restored.TryGetValue(entry, out var now))
                byPath[old.Trim()] = now;
        }
        return old =>
        {
            var key = old.Trim();
            if (byPath.TryGetValue(key, out var now))
                return now;
            if (manifest == null && restored.TryGetValue(CoversPrefix + FileNameOf(key), out now))
                return now;
            return File.Exists(key) ? key : null;
        };
    }

    private static void Verify(string zipPath, string snapshot, int covers)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var database = zip.GetEntry(DatabaseEntry);
        var saved = zip.Entries.Count(e => e.FullName.StartsWith(CoversPrefix, StringComparison.Ordinal));
        if (database == null || zip.GetEntry(ManifestEntry) == null || saved != covers || !SameStream(database, snapshot))
            throw new BackupException("The backup did not read back correctly. Try saving it again, or to another drive.");
    }

    private static bool SameStream(ZipArchiveEntry entry, string path)
    {
        using var saved = entry.Open();
        using var original = File.OpenRead(path);
        var a = new byte[81920];
        var b = new byte[81920];
        while (true)
        {
            var read = original.ReadAtLeast(a, a.Length, throwOnEndOfStream: false);
            var back = saved.ReadAtLeast(b, read == 0 ? 1 : read, throwOnEndOfStream: false);
            if (read != back || !a.AsSpan(0, read).SequenceEqual(b.AsSpan(0, back)))
                return false;
            if (read == 0)
                return true;
        }
    }

    private static BackupManifest? ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestEntry);
        if (entry == null)
            return null;
        try
        {
            using var input = entry.Open();
            return JsonSerializer.Deserialize<BackupManifest>(input, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    private static FileKind KindOf(string path)
    {
        var header = new byte[16];
        int read;
        using (var file = File.OpenRead(path))
            read = file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read >= 4 && header[0] == 'P' && header[1] == 'K' && header[2] == 3 && header[3] == 4)
            return FileKind.Zip;
        if (read == header.Length && Encoding.ASCII.GetString(header) == "SQLite format 3\0")
            return FileKind.Database;
        return FileKind.Other;
    }

    private static string? CoverName(ZipArchiveEntry entry)
    {
        if (!entry.FullName.StartsWith(CoversPrefix, StringComparison.Ordinal) || entry.Length > MaxCoverBytes)
            return null;
        var name = entry.FullName[CoversPrefix.Length..];
        return IsPlainFileName(name) ? name : null;
    }

    /// <summary>A name that stays inside the covers folder on every system.</summary>
    private static bool IsPlainFileName(string name) =>
        name.Length is > 0 and <= 180
        && name != "." && name != ".."
        && !name.EndsWith('.')
        && name.Trim() == name
        && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string EntryName(string name, HashSet<string> used)
    {
        if (!IsPlainFileName(name))
            name = "cover" + (IsPlainFileName(Path.GetExtension(name)) ? Path.GetExtension(name) : "");
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++)
            candidate = stem + "-" + n + extension;
        return candidate;
    }

    /// <summary>Takes the name after the last slash of either kind, because a backup made on Windows can be read anywhere.</summary>
    private static string FileNameOf(string path)
    {
        var trimmed = path.Trim();
        var cut = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    private static byte[]? ReadCover(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var memory = new MemoryStream();
        input.CopyTo(memory);
        return memory.ToArray();
    }

    private static bool SameBytes(string path, byte[] bytes)
    {
        try
        {
            return new FileInfo(path).Length == bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string FreePath(string folder, string stem, string extension)
    {
        var path = Path.Combine(folder, stem + extension);
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, stem + "-" + n + extension);
        return path;
    }

    private static string WorkFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "YourLocalLib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .part file is harmless and is replaced by the next save.
        }
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows clears its temp folder on its own.
        }
    }

    private static BackupException NotABackup() =>
        new("That file is not a library backup. Choose a .zip file made with Save a backup, or a library-….db file from the backups folder.");

    private static BackupException Newer() =>
        new("This backup was made by a newer version of " + AppBrand.Name + ". Install the newest version, then restore it.");

    private static BackupException Damaged(Exception? inner = null) =>
        new("This backup is damaged and cannot be restored. Nothing was changed.", inner);

    private enum FileKind
    {
        Other,
        Zip,
        Database
    }

    private sealed record Prepared(string Work, string Database, ZipArchive? Zip, BackupManifest? Manifest, BackupCheck Check) : IDisposable
    {
        public void Dispose()
        {
            Zip?.Dispose();
            TryDeleteFolder(Work);
        }
    }

    private sealed class BackupManifest
    {
        public int Format { get; set; } = LibraryBackup.Format;
        public string App { get; set; } = AppBrand.Name;
        public string? AppVersion { get; set; }
        public int Schema { get; set; }
        public DateTime? CreatedUtc { get; set; }

        /// <summary>Each cover path in the library, mapped to its entry in this zip.</summary>
        public Dictionary<string, string> Covers { get; set; } = new(StringComparer.Ordinal);
    }
}
