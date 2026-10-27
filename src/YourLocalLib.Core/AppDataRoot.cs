namespace YourLocalLib.Core;

/// <summary>
/// Catalog folder under LocalApplicationData. An older install named IrishsLibraryTracker
/// is moved here once, so the books stay with the renamed program.
/// </summary>
public static class AppDataRoot
{
    public const string FolderName = "YourLocalLib";
    public const string PreviousFolderName = "IrishsLibraryTracker";

    public static string Resolve() =>
        Resolve(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static string Resolve(string localAppData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        var current = Path.Combine(localAppData, FolderName);
        var previous = Path.Combine(localAppData, PreviousFolderName);
        if (!Directory.Exists(current) && Directory.Exists(previous))
        {
            try
            {
                Directory.Move(previous, current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return previous;
            }
        }

        Directory.CreateDirectory(current);
        return current;
    }
}
