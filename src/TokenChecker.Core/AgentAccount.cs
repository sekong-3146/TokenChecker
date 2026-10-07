namespace TokenChecker.Core;

public enum AgentKind
{
    Codex,
    Claude
}

public sealed class AgentAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public AgentKind Kind { get; set; }

    public string Name { get; set; } = "Codex";

    public bool Enabled { get; set; } = true;

    public bool UseDefaultCredentials { get; set; }

    public AgentAccount Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Name = Name,
        Enabled = Enabled,
        UseDefaultCredentials = UseDefaultCredentials
    };

    public static AgentAccount Create(AgentKind kind)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return new AgentAccount
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = kind,
            Name = GetDefaultName(kind),
            Enabled = true,
            UseDefaultCredentials = false
        };
    }

    public string GetConfigDirectory()
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new InvalidOperationException("계정 종류가 올바르지 않습니다.");
        }

        if (UseDefaultCredentials)
        {
            var environmentName = Kind == AgentKind.Codex ? "CODEX_HOME" : "CLAUDE_CONFIG_DIR";
            var configured = Environment.GetEnvironmentVariable(environmentName);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.GetFullPath(configured);
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Kind == AgentKind.Codex ? ".codex" : ".claude");
        }

        if (!IsSafeId(Id))
        {
            throw new InvalidOperationException("계정 ID가 올바르지 않습니다.");
        }

        var accountsDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TokenChecker",
            "accounts"));
        var accountDirectory = Path.GetFullPath(Path.Combine(
            accountsDirectory,
            Id,
            Kind.ToString().ToLowerInvariant()));
        var requiredPrefix = accountsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                             Path.DirectorySeparatorChar;
        if (!accountDirectory.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("계정 설정 경로가 올바르지 않습니다.");
        }

        return accountDirectory;
    }

    internal static bool IsSafeId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.Length <= 128 &&
        id.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    internal static string GetDefaultName(AgentKind kind) => kind == AgentKind.Codex ? "Codex" : "Claude";
}
