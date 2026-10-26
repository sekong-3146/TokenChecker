using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenChecker.Core;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public SettingsStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TokenChecker",
            "settings.json");
    }

    public string Path { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(Path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            return Normalize(settings ?? new AppSettings(), migrateLegacy: true);
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ValidateForSave(settings);
        var normalized = Normalize(settings, migrateLegacy: true);
        var directory = System.IO.Path.GetDirectoryName(Path);
        var temporaryPath = System.IO.Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(string.IsNullOrEmpty(directory) ? "." : directory);
            var json = JsonSerializer.Serialize(normalized, SerializerOptions);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, Path, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions() => new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static AppSettings Normalize(AppSettings settings, bool migrateLegacy)
    {
        var normalized = settings.Clone();
        normalized.RefreshIntervalMinutes = normalized.RefreshIntervalMinutes switch
        {
            <= 3 => 3,
            <= 5 => 5,
            _ => 10
        };
        normalized.WarnThresholdPercent = Math.Clamp(normalized.WarnThresholdPercent, 1, 100);
        normalized.CriticalThresholdPercent = Math.Clamp(normalized.CriticalThresholdPercent, 1, 100);
        if (normalized.WarnThresholdPercent > normalized.CriticalThresholdPercent)
        {
            normalized.CriticalThresholdPercent = normalized.WarnThresholdPercent;
        }

        normalized.PopupTransparencyPercent = Math.Clamp(
            normalized.PopupTransparencyPercent,
            0,
            AppSettings.MaxPopupTransparencyPercent);

        normalized.TaskbarOverlayScreen = string.IsNullOrWhiteSpace(normalized.TaskbarOverlayScreen)
            ? null
            : normalized.TaskbarOverlayScreen.Trim();

        if (normalized.Agents is null && migrateLegacy)
        {
            normalized.Agents = normalized.GetAgents().Select(account => account.Clone()).ToList();
        }

        if (normalized.Agents is not null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            normalized.Agents = normalized.Agents
                .Where(account => account is not null)
                .Where(account => Enum.IsDefined(account.Kind))
                .Select(account => NormalizeAccount(account!))
                .Where(account => seen.Add(account.Id))
                .Take(AppSettings.MaxAgents)
                .ToList();
        }

        return normalized;
    }

    private static AgentAccount NormalizeAccount(AgentAccount account)
    {
        var normalized = account.Clone();
        if (!AgentAccount.IsSafeId(normalized.Id))
        {
            normalized.Id = Guid.NewGuid().ToString("N");
        }

        normalized.Name = string.IsNullOrWhiteSpace(normalized.Name)
            ? AgentAccount.GetDefaultName(normalized.Kind)
            : normalized.Name.Trim();
        return normalized;
    }

    private static void ValidateForSave(AppSettings settings)
    {
        if (settings.Agents is null)
        {
            return;
        }

        if (settings.Agents.Count > AppSettings.MaxAgents)
        {
            throw new ArgumentException($"계정은 최대 {AppSettings.MaxAgents}개까지 추가할 수 있습니다.", nameof(settings));
        }

        if (settings.Agents.Any(account =>
                account is null ||
                !Enum.IsDefined(account.Kind) ||
                !AgentAccount.IsSafeId(account.Id)))
        {
            throw new ArgumentException("계정 ID가 올바르지 않습니다.", nameof(settings));
        }

        if (settings.Agents
            .GroupBy(account => account.Id, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            throw new ArgumentException("중복된 계정 ID는 저장할 수 없습니다.", nameof(settings));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A failed cleanup must not hide the original persistence failure.
        }
    }
}
