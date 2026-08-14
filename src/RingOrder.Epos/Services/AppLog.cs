using System.Collections.Concurrent;
using System.Text;
using RingOrder.Epos.Data;

namespace RingOrder.Epos.Services;

/// <summary>
/// The till's log file.
/// <para>
/// Support happens over a remote session on a merchant's PC, where a console
/// window does not exist. Everything that used to be written to the console now
/// lands in a dated file under <c>logs/</c>, and that file is what gets read
/// when a shop says a ticket did not print at seven o'clock last Friday.
/// </para>
/// <para>
/// Hand-rolled rather than pulled from a package: it needs to append a line,
/// roll at midnight, delete old files and never — under any circumstance —
/// throw. A till must not fall over because a log file is locked.
/// </para>
/// </summary>
public static class AppLog
{
    private const int KeepDays = 30;

    private static readonly object Gate = new();
    private static DateOnly _prunedFor;

    /// <summary>The most recent lines, for the diagnostics screen.</summary>
    public static IReadOnlyCollection<string> Recent => RecentLines;
    private static readonly ConcurrentQueue<string> RecentLines = new();

    /// <summary>
    /// Why the last write failed, if it did. Surfaced in diagnostics: a log that
    /// silently stops is worse than no log, because it is trusted.
    /// </summary>
    public static string? LastWriteError { get; private set; }

    public static string Directory => LocalPaths.LogDirectory;

    public static void Start()
    {
        Info("app", $"started, version {AppVersion}");
    }

    public static string AppVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "dev";

    public static void Info(string area, string message) => Write("INFO", area, message);
    public static void Warn(string area, string message) => Write("WARN", area, message);

    public static void Error(string area, string message, Exception? ex = null) =>
        Write("ERROR", area, ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    /// <summary>A logger for a component that should not know where logs go.</summary>
    public static Action<string> For(string area) => message => Info(area, message);

    /// <summary>
    /// Appends under a lock, on the calling thread.
    /// <para>
    /// A till writes a few hundred lines an hour. Buffering that onto a
    /// background worker bought nothing and cost something real: the first
    /// version queued lines that were never drained, so the log looked healthy
    /// with one line in it while everything after startup vanished. Writing
    /// straight through cannot half-work.
    /// </para>
    /// </summary>
    private static void Write(string level, string area, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} [{area}] {message}";

        RecentLines.Enqueue(line);
        while (RecentLines.Count > 300) RecentLines.TryDequeue(out _);

        lock (Gate)
        {
            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (today != _prunedFor)
                {
                    _prunedFor = today;
                    Prune();
                }

                File.AppendAllText(PathForDay(today), line + Environment.NewLine, Encoding.UTF8);
                LastWriteError = null;
            }
            catch (Exception ex)
            {
                // Disk full, folder removed, a virus scanner holding the file —
                // none of these may reach the counter, but all of them must be
                // visible to whoever is looking at diagnostics.
                LastWriteError = $"{ex.GetType().Name}: {ex.Message}";
            }
        }
    }

    private static string PathForDay(DateOnly day) =>
        Path.Combine(Directory, $"epos-{day:yyyy-MM-dd}.log");

    private static void Prune()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-KeepDays);
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "epos-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch
        {
            // Housekeeping is never worth an exception.
        }
    }

    /// <summary>
    /// Everything a support call needs, in one file to send us: versions, the
    /// shop, the schema, printers, disk, and the recent log.
    /// </summary>
    public static string ExportDiagnostics(AppServices app)
    {
        var report = new StringBuilder();
        void Section(string title) => report.AppendLine().AppendLine($"── {title} ──");

        report.AppendLine($"RingOrder EPOS diagnostics — {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        Section("Application");
        report.AppendLine($"version      {AppVersion}");
        report.AppendLine($"machine      {Environment.MachineName}");
        report.AppendLine($"windows      {Environment.OSVersion}");
        report.AppendLine($"data         {LocalPaths.RootDirectory}");

        Section("Shop");
        var settings = app.GetSettings();
        report.AppendLine($"name         {settings.ShopName}");
        report.AppendLine($"dishes       {app.Menu.CountItems()}");
        report.AppendLine($"staff        {app.Staff.CountActive()} active");

        Section("Database");
        try
        {
            using var conn = app.Db.Open();
            report.AppendLine($"schema       {SchemaMigrations.CurrentVersion(conn)} of {SchemaMigrations.LatestVersion}");
            using var check = conn.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            report.AppendLine($"integrity    {check.ExecuteScalar()}");
            report.AppendLine($"size         {new FileInfo(app.Db.Path).Length / 1024} KB");
        }
        catch (Exception ex)
        {
            report.AppendLine($"unreadable   {ex.Message}");
        }

        Section("Printers");
        foreach (var device in app.PrintDevices.GetDevices())
        {
            var fault = app.PrintQueue.Faults.TryGetValue(device.Id, out var f) ? $"  FAULT: {f}" : "";
            report.AppendLine(
                $"{(device.IsEnabled ? "on " : "off")} {device.Name,-18} {device.Transport,-14} {device.Address}{fault}");
        }
        report.AppendLine($"queue        {app.PrintJobs.CountWaiting()} waiting, {app.PrintJobs.GetAbandoned().Count} given up");

        Section("Web orders");
        report.AppendLine($"polling      {app.OnlinePoller.IsRunning}");
        report.AppendLine($"last status  {app.OnlinePoller.LastStatus}");

        Section("Shift");
        report.AppendLine(app.Session.Shift is { } shift
            ? $"open         #{shift.Number} since {shift.OpenedAt:HH:mm}"
            : "open         none");

        Section("Backups");
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(LocalPaths.BackupDirectory)
                         .OrderByDescending(File.GetLastWriteTime).Take(5))
                report.AppendLine($"{File.GetLastWriteTime(file):yyyy-MM-dd HH:mm}  {Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            report.AppendLine(ex.Message);
        }

        Section("Logging");
        report.AppendLine($"folder       {Directory}");
        report.AppendLine($"last error   {LastWriteError ?? "none"}");

        Section("Recent log");
        foreach (var line in RecentLines) report.AppendLine(line);

        var path = Path.Combine(Directory, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, report.ToString(), Encoding.UTF8);
        return path;
    }
}
