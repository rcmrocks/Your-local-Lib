using System.Net;

namespace YourLocalLib.Core;

public enum LookupStatus
{
    Found,
    NotFound,
    Limited,
    Failed
}

public static class LookupStatusExtensions
{
    /// <summary>The site gave a real answer, so asking again later would not change it.</summary>
    public static bool Answered(this LookupStatus status) => status is LookupStatus.Found or LookupStatus.NotFound;
}

public sealed record WebAnswer(LookupStatus Status, byte[]? Body = null);

public sealed record CoverFile(LookupStatus Status, string? Path);

public sealed class WebSource
{
    public const string UserAgent = AppBrand.UserAgent;
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(10);
    private static readonly string[] ImageExtensions = { ".jpg", ".png", ".webp" };
    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _pausedUntil = new(StringComparer.OrdinalIgnoreCase);

    public WebSource(HttpClient? http = null, IClock? clock = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _clock = clock ?? new SystemClock();
    }

    public async Task<WebAnswer> GetAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return new WebAnswer(LookupStatus.NotFound);
        var site = SiteOf(uri);
        if (IsPaused(site))
            return new WebAnswer(LookupStatus.Limited);
        try
        {
            using var response = await _http.GetAsync(uri, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                PauseSite(site);
                return new WebAnswer(LookupStatus.Limited);
            }
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return new WebAnswer(LookupStatus.NotFound);
            if (!response.IsSuccessStatusCode)
                return new WebAnswer(LookupStatus.Failed);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return new WebAnswer(LookupStatus.Found, body);
        }
        catch (HttpRequestException)
        {
            return new WebAnswer(LookupStatus.Failed);
        }
        catch (IOException)
        {
            return new WebAnswer(LookupStatus.Failed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebAnswer(LookupStatus.Failed);
        }
    }

    /// <summary>Saves the image as {isbn13}.jpg, .png, or .webp. A valid file already saved for that ISBN is reused without asking the site.</summary>
    public async Task<CoverFile> CacheImageAsync(string url, string folder, string isbn13, CancellationToken cancellationToken = default)
    {
        foreach (var ext in ImageExtensions)
        {
            var existing = Path.Combine(folder, isbn13 + ext);
            if (ReusableImage(existing))
                return new CoverFile(LookupStatus.Found, existing);
        }

        var answer = await GetAsync(url, cancellationToken);
        if (answer.Status != LookupStatus.Found)
            return new CoverFile(answer.Status, null);
        var bytes = answer.Body ?? Array.Empty<byte>();
        var extension = CoverExtension(bytes, bytes.Length);
        if (extension == null)
            return new CoverFile(LookupStatus.NotFound, null);
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, isbn13 + extension);
            var part = path + ".part";
            await File.WriteAllBytesAsync(part, bytes, cancellationToken);
            File.Move(part, path, true);
            return new CoverFile(LookupStatus.Found, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CoverFile(LookupStatus.Failed, null);
        }
    }

    public static string? CoverExtension(ReadOnlySpan<byte> header, long length)
    {
        if (length < 2000)
            return null;
        if (header.Length >= 2 && header[0] == 0xFF && header[1] == 0xD8)
            return ".jpg";
        if (header.Length >= 4 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            return ".png";
        if (header.Length >= 12
            && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P')
            return ".webp";
        return null;
    }

    private static bool ReusableImage(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            var length = new FileInfo(path).Length;
            var header = new byte[12];
            int read;
            using (var stream = File.OpenRead(path))
                read = stream.Read(header, 0, header.Length);
            return CoverExtension(header.AsSpan(0, read), length) != null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Open Library limits its ISBN cover address separately, so a refusal there leaves covers by ID alone.
    private static string SiteOf(Uri uri) =>
        uri.AbsolutePath.StartsWith("/b/isbn/", StringComparison.OrdinalIgnoreCase) ? uri.Host + "/b/isbn" : uri.Host;

    private bool IsPaused(string site)
    {
        lock (_gate)
        {
            if (!_pausedUntil.TryGetValue(site, out var until))
                return false;
            if (_clock.UtcNow < until)
                return true;
            _pausedUntil.Remove(site);
            return false;
        }
    }

    private void PauseSite(string site)
    {
        lock (_gate)
            _pausedUntil[site] = _clock.UtcNow + Pause;
    }
}
