using YourLocalLib.Core;
using Xunit;

namespace YourLocalLib.Tests;

public class AppDataRootTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "yll-root-" + Guid.NewGuid().ToString("N"));

    public AppDataRootTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* leftover temp files must not fail the run */ }
    }

    [Fact]
    public void Moves_the_old_catalog_folder_when_the_new_one_is_missing()
    {
        var old = Path.Combine(_dir, AppDataRoot.PreviousFolderName);
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "library.db"), "ok");

        var root = AppDataRoot.Resolve(_dir);

        Assert.Equal(Path.Combine(_dir, AppDataRoot.FolderName), root);
        Assert.True(File.Exists(Path.Combine(root, "library.db")));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void Leaves_the_new_folder_when_both_exist()
    {
        var old = Path.Combine(_dir, AppDataRoot.PreviousFolderName);
        var current = Path.Combine(_dir, AppDataRoot.FolderName);
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(old, "old.db"), "old");
        File.WriteAllText(Path.Combine(current, "library.db"), "new");

        var root = AppDataRoot.Resolve(_dir);

        Assert.Equal(current, root);
        Assert.True(File.Exists(Path.Combine(old, "old.db")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(current, "library.db")));
    }

    [Fact]
    public void Creates_the_new_folder_when_neither_exists()
    {
        var root = AppDataRoot.Resolve(_dir);
        Assert.Equal(Path.Combine(_dir, AppDataRoot.FolderName), root);
        Assert.True(Directory.Exists(root));
    }
}
