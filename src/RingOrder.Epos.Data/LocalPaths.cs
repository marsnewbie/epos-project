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

    /// <summary>
    /// Where a bundle downloaded from the cloud lands, next to any that was put
    /// there by hand. Named so it is obvious which one arrived on its own.
    /// </summary>
    public static string CloudBundlePath => Path.Combine(ProfileDirectory, "cloud.ringpos.json");

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

    /// <summary>
    /// Set when the machine-wide location could not be used. Loud on purpose:
    /// a till quietly keeping its data somewhere else is how a shop loses a
    /// day's orders and nobody can say where they went.
    /// </summary>
    public static string? FallbackReason { get; private set; }

    private static string ResolveRoot()
    {
        var machineWide = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "RingOrder", "EPOS");

        // An installer that ran as administrator can leave ProgramData read-only
        // for a standard till account. Falling back keeps the app usable, and
        // the installer's ACL step is what makes the machine-wide path the real
        // one.
        if (TryPrepare(machineWide, out var reason))
            return machineWide;

        FallbackReason = reason;

        var perUser = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RingOrder", "EPOS");
        Directory.CreateDirectory(perUser);
        return perUser;
    }

    private static bool TryPrepare(string dir, out string? reason)
    {
        reason = null;
        try
        {
            Directory.CreateDirectory(dir);

            // The probe is named per process. A shared name meant two instances
            // starting together could delete each other's file, and the loser
            // decided ProgramData was unwritable and silently moved the shop's
            // database into its own user profile.
            var probe = Path.Combine(dir, $".write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            reason = ex.Message;
            return false;
        }
    }
}
