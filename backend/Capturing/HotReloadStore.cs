using Serilog;
using System.Text.Json;
using System.Reflection;

namespace ReVerse.Capture.Capturing;





public abstract class HotReloadStore<TEntry> : IDisposable where TEntry : class
{
    private readonly string _filePath;
    private readonly string? _defaultJson;
    private readonly FileSystemWatcher _watcher;
    private readonly object _lock = new();
    private Dictionary<string, TEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    protected HotReloadStore(string filePath, string? defaultJson = null)
    {
        _filePath = filePath;
        _defaultJson = defaultJson;
        var directory = Path.GetDirectoryName(filePath)!;
        var fileName = Path.GetFileName(filePath);
        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        try
        {

            Thread.Sleep(100);
            Load();
        }
        catch (Exception exception)
        {

            Log.ForContext("UserFacing", true).Warning(exception, "Response settings could not be reloaded. Keeping the previous settings.");
        }
    }

    protected abstract Dictionary<string, TEntry>? Deserialize(string json);

    public void Load()
    {
        if (!File.Exists(_filePath))
        {
            var defaults = _defaultJson is null ? null : Deserialize(_defaultJson);
            lock (_lock) { _entries = defaults ?? new Dictionary<string, TEntry>(StringComparer.OrdinalIgnoreCase); }
            return;
        }

        var parsed = Deserialize(File.ReadAllText(_filePath));
        if (parsed is not null)
        {
            lock (_lock) { _entries = parsed; }
        }
    }

    public TEntry? GetEntry(string? path)
    {
        lock (_lock)
        {
            if (path is not null && _entries.TryGetValue(path, out var entry))
                return entry;
        }
        return null;
    }

    public void Dispose() => _watcher.Dispose();
}

public sealed class ResponseOverrideEntry
{
    public int StatusCode { get; set; } = 200;
    public string? Body { get; set; }
    public string? ContentType { get; set; }
}

public sealed class ContractEntry
{
    public string? Body { get; set; }
}


public sealed class ResponseOverrides : HotReloadStore<ResponseOverrideEntry>
{
    public ResponseOverrides(string filePath, string? defaultJson = null) : base(filePath, defaultJson) { }
    protected override Dictionary<string, ResponseOverrideEntry>? Deserialize(string json) =>
        JsonSerializer.Deserialize(json, Serialization.SourceGenerationContext.Default.DictionaryStringResponseOverrideEntry);
}


public sealed class ContractOverrides : HotReloadStore<ContractEntry>
{
    public ContractOverrides(string filePath, string? defaultJson = null) : base(filePath, defaultJson) { }
    protected override Dictionary<string, ContractEntry>? Deserialize(string json) =>
        JsonSerializer.Deserialize(json, Serialization.SourceGenerationContext.Default.DictionaryStringContractEntry);
}

public static class BundledResources
{
    public static string Read(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith($".{fileName}", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName) ??
            throw new InvalidOperationException($"Resource {fileName} was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
