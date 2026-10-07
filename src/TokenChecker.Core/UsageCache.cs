using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenChecker.Core;

public sealed class UsageCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(48);

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public UsageCache(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TokenChecker",
            "usage-cache.json");
    }

    public string Path { get; }

    public IReadOnlyList<UsageSnapshot> Load(DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(Path))
            {
                return Array.Empty<UsageSnapshot>();
            }

            var json = File.ReadAllText(Path);
            var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json, SerializerOptions);
            if (entries is null)
            {
                return Array.Empty<UsageSnapshot>();
            }

            return entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ProviderId) &&
                                now - entry.RetrievedAt <= MaxAge)
                .Select(entry => new UsageSnapshot(
                    entry.ProviderId!,
                    ProviderStatus.Stale,
                    entry.Windows ?? Array.Empty<UsageWindow>(),
                    entry.RetrievedAt,
                    DisplayName: entry.DisplayName,
                    ProviderKind: entry.ProviderKind,
                    PlanType: entry.PlanType))
                .ToArray();
        }
        catch
        {
            return Array.Empty<UsageSnapshot>();
        }
    }

    public void Save(IEnumerable<UsageSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var temporaryPath = string.Empty;
        try
        {
            var entries = snapshots.Select(snapshot => new CacheEntry
            {
                ProviderId = snapshot.ProviderId,
                DisplayName = snapshot.DisplayName,
                ProviderKind = snapshot.ProviderKind,
                PlanType = snapshot.PlanType,
                Windows = snapshot.Windows ?? Array.Empty<UsageWindow>(),
                RetrievedAt = snapshot.RetrievedAt
            }).ToArray();

            var directory = System.IO.Path.GetDirectoryName(Path);
            var directoryPath = string.IsNullOrEmpty(directory) ? "." : directory;
            temporaryPath = System.IO.Path.Combine(
                directoryPath,
                $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");

            Directory.CreateDirectory(directoryPath);
            var json = JsonSerializer.Serialize(entries, SerializerOptions);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, Path, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions() => new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed class CacheEntry
    {
        public string? ProviderId { get; set; }

        public string? DisplayName { get; set; }

        public string? ProviderKind { get; set; }

        public string? PlanType { get; set; }

        public IReadOnlyList<UsageWindow>? Windows { get; set; }

        public DateTimeOffset RetrievedAt { get; set; }
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Persistence errors are intentionally non-fatal.
        }
    }
}
