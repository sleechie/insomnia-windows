using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Insomnia.Core;

/// <summary>
/// Where everything lives: <c>%LOCALAPPDATA%\Insomnia</c>. The folder is
/// per-user and inherits the profile's owner-only ACL. <c>INSOMNIA_HOME</c>
/// overrides it (tests, and side-by-side builds).
/// </summary>
public sealed class Paths
{
    public string Root { get; }
    public string Config => Path.Combine(Root, "config.json");
    public string Session => Path.Combine(Root, "session.json");
    public string State => Path.Combine(Root, "state.json");
    public string Lock => Path.Combine(Root, "recovery.lock");
    public string Logs => Path.Combine(Root, "Logs");
    public string Log => Path.Combine(Logs, "insomnia.log");
    public string Handoffs => Path.Combine(Logs, "handoffs.log");
    public string LidSimulation => Path.Combine(Root, "lid-simulation");

    public Paths(string? root = null)
    {
        Root = root
            ?? Environment.GetEnvironmentVariable("INSOMNIA_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Insomnia");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }

    public static Paths Default { get; set; } = new();
}

/// <summary>JSON on disk, written atomically (temp file, flush, rename).</summary>
public static class JsonStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static void WriteAtomic<T>(string path, T value)
    {
        var tmp = path + ".tmp-" + Environment.ProcessId;
        var bytes = Encoding.UTF8.GetBytes(Serialize(value));
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(bytes);
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public enum ReadResult { Ok, Missing, Unreadable }

    /// <summary>Reads a file. A file that exists but does not parse is <see cref="ReadResult.Unreadable"/>, never "missing".</summary>
    public static ReadResult TryRead<T>(string path, out T? value) where T : class
    {
        value = null;
        if (!File.Exists(path)) return Directory.Exists(path) ? ReadResult.Unreadable : ReadResult.Missing;
        try
        {
            value = Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
            return value is null ? ReadResult.Unreadable : ReadResult.Ok;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ReadResult.Unreadable;
        }
    }
}

/// <summary>
/// Session, state and config as one unit. An unreadable journal refuses
/// every transaction (it is evidence, never treated as clean).
/// </summary>
public sealed class Store(Paths paths)
{
    public Paths Paths { get; } = paths;

    public Config LoadConfig(out string? note)
    {
        note = null;
        switch (JsonStore.TryRead<Config>(Paths.Config, out var cfg))
        {
            case JsonStore.ReadResult.Ok:
                note = cfg!.NormalizeFloors();
                if (note is not null) SaveConfig(cfg);
                return cfg;
            case JsonStore.ReadResult.Missing:
                cfg = new Config();
                SaveConfig(cfg);
                return cfg;
            default:
                var aside = SetAside(Paths.Config);
                note = $"config.json did not parse; moved to {aside} and started from defaults";
                cfg = new Config();
                SaveConfig(cfg);
                return cfg;
        }
    }

    public void SaveConfig(Config c) => JsonStore.WriteAtomic(Paths.Config, c);

    public JsonStore.ReadResult LoadSession(out Session? s)
    {
        var r = JsonStore.TryRead(Paths.Session, out s);
        if (r == JsonStore.ReadResult.Ok && !s!.IsValid) { s = null; return JsonStore.ReadResult.Unreadable; }
        return r;
    }

    public void SaveSession(Session s) => JsonStore.WriteAtomic(Paths.Session, s);
    public void DeleteSession() { if (File.Exists(Paths.Session)) File.Delete(Paths.Session); }

    /// <exception cref="UnreadableJournalException">state.json exists but does not parse.</exception>
    public RuntimeState LoadState()
    {
        return JsonStore.TryRead<RuntimeState>(Paths.State, out var st) switch
        {
            JsonStore.ReadResult.Ok => st!,
            JsonStore.ReadResult.Missing => new RuntimeState(),
            _ => throw new UnreadableJournalException(Paths.State),
        };
    }

    public void SaveState(RuntimeState s) => JsonStore.WriteAtomic(Paths.State, s);

    /// <summary>
    /// Renames a file to <c>name.unreadable-&lt;UTC stamp&gt;</c> beside it, never
    /// overwriting an earlier copy. Returns the new path.
    /// </summary>
    public static string SetAside(string path)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        var target = $"{path}.unreadable-{stamp}";
        for (var i = 1; File.Exists(target) || Directory.Exists(target); i++) target = $"{path}.unreadable-{stamp}-{i}";
        File.Move(path, target);
        return target;
    }
}

public sealed class UnreadableJournalException(string path)
    : Exception($"{path} exists but cannot be read. It is kept as evidence; Insomnia will not change anything until it is fixed or moved aside.")
{
    public string JournalPath { get; } = path;
}

/// <summary>
/// The shared lock the app, the recovery task and uninstall take before
/// touching the journal or the machine. An exclusive handle on
/// <c>recovery.lock</c>; the OS releases it if the holder dies.
/// </summary>
public sealed class RecoveryLock : IDisposable
{
    readonly FileStream _fs;
    RecoveryLock(FileStream fs) => _fs = fs;

    public static RecoveryLock? TryAcquire(Paths paths, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                var fs = new FileStream(paths.Lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                fs.SetLength(0);
                var bytes = Encoding.UTF8.GetBytes($"{Environment.ProcessId}\n");
                fs.Write(bytes);
                fs.Flush();
                return new RecoveryLock(fs);
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline) return null;
                Thread.Sleep(50);
            }
        }
    }

    public void Dispose() => _fs.Dispose();
}

/// <summary>Plain-text logs, each capped at 1 MiB with one older copy kept as <c>.1</c>.</summary>
public static class Log
{
    static readonly object Gate = new();
    public const long Cap = 1024 * 1024;
    public static string Tag { get; set; } = "app";

    public static void Info(string msg) => Write(Paths.Default.Log, "info", msg);
    public static void Error(string msg) => Write(Paths.Default.Log, "error", msg);
    public static void Handoff(string msg) => Write(Paths.Default.Handoffs, "gap", msg);

    static void Write(string path, string level, string msg)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} [{Tag}] {level}: {msg}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > Cap && fi.LinkTarget is null)
                    File.Move(path, path + ".1", overwrite: true);
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var bytes = Encoding.UTF8.GetBytes(line);
                fs.Write(bytes);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
#if DEBUG
        System.Diagnostics.Debug.Write(line);
#endif
    }
}
