namespace RingOrder.Epos.Data;

/// <summary>
/// Where a till keeps its data on the merchant's PC.
/// <para>
/// Machine-wide under <c>%PROGRAMDATA%\RingOrder\EPOS</c>, not per-user: a shop
/// signing into a second Windows account must not find an empty till, and the
/// support scripts need one predictable path to back up and inspect.
/// </para>
/// </summary>
public static class LocalPaths
{
    private static string? _root;

    /// <summary>Root data directory, created on first use.</summary>
    public static string RootDirectory => _root ??= ResolveRoot();

    /// <summary>Runtime database — the till's source of truth.</summary>
    public static string DatabasePath => Path.Combine(RootDirectory, "data.sqlite");

    /// <summary>Imported shop bundle, kept for reference and re-import.</summary>
    public static string ProfileDirectory => EnsureSubdirectory("profile");

    /// <summary>Nightly database copies.</summary>
    public static string BackupDirectory => EnsureSubdirectory("backups");

    /// <summary>Rolling application logs, collected by the diagnostics export.</summary>
    public static string LogDirectory => EnsureSubdirectory("logs");

    private static string EnsureSubdirectory(string name)
    {
        var dir = Path.Combine(RootDirectory, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ResolveRoot()
    {
        var machineWide = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RingOrder", "EPOS");

        // An installer that ran as administrator can leave ProgramData read-only
        // for a standard till account. Falling back keeps the app usable and the
        // installer's ACL step is what makes the machine-wide path the real one.
        if (TryPrepare(machineWide))
            return machineWide;

        var perUser = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RingOrder", "EPOS");
        Directory.CreateDirectory(perUser);
        return perUser;
    }

    private static bool TryPrepare(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
