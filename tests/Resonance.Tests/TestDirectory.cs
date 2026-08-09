namespace Resonance.Tests;

internal static class TestDirectory
{
    private static readonly object Sync = new();
    private static readonly HashSet<string> Deferred = [];

    static TestDirectory()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteDeferred();
    }

    public static void Delete(string path, bool recursive = true)
    {
        try
        {
            System.IO.Directory.Delete(path, recursive);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            lock (Sync)
                Deferred.Add(path);
        }
    }

    public static DirectoryInfo CreateDirectory(string path) => System.IO.Directory.CreateDirectory(path);

    public static DirectoryInfo? GetParent(string path) => System.IO.Directory.GetParent(path);

    public static IEnumerable<string> EnumerateFiles(string path) => System.IO.Directory.EnumerateFiles(path);

    public static IEnumerable<string> EnumerateFiles(string path, string searchPattern) =>
        System.IO.Directory.EnumerateFiles(path, searchPattern);

    private static void DeleteDeferred()
    {
        lock (Sync)
        {
            foreach (var path in Deferred)
            {
                try
                {
                    System.IO.Directory.Delete(path, recursive: true);
                }
                catch (IOException)
                {
                    // The runner owns its temporary directory and performs final cleanup.
                }
            }
        }
    }
}
