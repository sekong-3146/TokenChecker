using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

return await UsageProbe.RunAsync(args);

internal static class UsageProbe
{
    private const int CodexStartTimeoutSeconds = 10;
    private const int RequestTimeoutSeconds = 20;
    private const string ClaudeUsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string ClaudeOauthBeta = "oauth-2025-04-20";

    public static async Task<int> RunAsync(string[] args)
    {
        var target = args.SingleOrDefault() ?? "--all";

        return target switch
        {
            "--codex" => await ProbeCodexAsync(),
            "--claude" => await ProbeClaudeAsync(),
            "--all" => await ProbeAllAsync(),
            _ => PrintUsage()
        };
    }

    private static async Task<int> ProbeAllAsync()
    {
        var codexTask = ProbeCodexAsync();
        var claudeTask = ProbeClaudeAsync();
        var results = await Task.WhenAll(codexTask, claudeTask);

        return results.All(result => result == 0) ? 0 : 1;
    }

    private static int PrintUsage()
    {
        Console.Error.WriteLine("Usage: UsageProbe [--codex|--claude|--all]");
        return 2;
    }

    private static async Task<int> ProbeCodexAsync()
    {
        var executable = FindCodexExecutable();
        if (executable is null)
        {
            Console.WriteLine("Codex: NotInstalled");
            return 1;
        }

        try
        {
            using var process = StartCodexAppServer(executable);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(CodexStartTimeoutSeconds));

            await WriteJsonLineAsync(process, new
            {
                method = "initialize",
                id = 1,
                @params = new
                {
                    clientInfo = new
                    {
                        name = "token_checker_usage_probe",
                        title = "TokenChecker UsageProbe",
                        version = "0.1.0"
                    }
                }
            }, timeout.Token);
            await WriteJsonLineAsync(process, new { method = "initialized", @params = new { } }, timeout.Token);
            await WriteJsonLineAsync(process, new { method = "account/rateLimits/read", id = 2 }, timeout.Token);

            while (!timeout.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                if (TryPrintCodexRateLimits(document.RootElement))
                {
                    return 0;
                }

                if (TryGetError(document.RootElement, out var error))
                {
                    Console.WriteLine($"Codex: Error ({error})");
                    return 1;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Codex: Timeout");
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Codex: Error ({ex.GetType().Name})");
            return 1;
        }

        Console.WriteLine("Codex: NoUsageResponse");
        return 1;
    }

    private static async Task<int> ProbeClaudeAsync()
    {
        var credentialPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            ".credentials.json");

        if (!File.Exists(credentialPath))
        {
            Console.WriteLine("Claude: SignedOut");
            return 1;
        }

        string? accessToken;
        try
        {
            await using var stream = File.OpenRead(credentialPath);
            using var document = await JsonDocument.ParseAsync(stream);
            accessToken = document.RootElement
                .GetProperty("claudeAiOauth")
                .GetProperty("accessToken")
                .GetString();
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine("Claude: CredentialFormatError");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            Console.WriteLine("Claude: SignedOut");
            return 1;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds) };
            using var request = new HttpRequestMessage(HttpMethod.Get, ClaudeUsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("anthropic-beta", ClaudeOauthBeta);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Claude: Http{(int)response.StatusCode}");
                return 1;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            PrintClaudeUsage(document.RootElement);
            return 0;
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("Claude: Timeout");
            return 1;
        }
        catch (HttpRequestException)
        {
            Console.WriteLine("Claude: NetworkError");
            return 1;
        }
        finally
        {
            accessToken = null;
        }
    }

    private static string? FindCodexExecutable()
    {
        var candidates = new List<string>();
        var configured = Environment.GetEnvironmentVariable("TOKEN_CHECKER_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }

        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "OpenAI",
            "Codex",
            "bin",
            "codex.exe"));

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path.Trim(), "codex.exe")));

        var executable = candidates.FirstOrDefault(File.Exists);
        if (executable is not null)
        {
            return executable;
        }

        return pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path.Trim(), "codex.cmd"))
            .FirstOrDefault(File.Exists);
    }

    private static Process StartCodexAppServer(string executable)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : executable,
            Arguments = Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                ? $"/d /s /c \"\"{executable}\" app-server\""
                : "app-server",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8
        };

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Codex app-server.");
    }

    private static async Task WriteJsonLineAsync(Process process, object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static bool TryPrintCodexRateLimits(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || id.GetInt32() != 2 ||
            !root.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("rateLimits", out var rateLimits))
        {
            return false;
        }

        Console.WriteLine("Codex: Ready");
        PrintCodexWindow(rateLimits, "primary", "Primary");
        PrintCodexWindow(rateLimits, "secondary", "Secondary");
        return true;
    }

    private static void PrintCodexWindow(JsonElement rateLimits, string propertyName, string label)
    {
        if (!rateLimits.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var usedPercent = ReadNumber(window, "usedPercent");
        var durationMinutes = ReadNumber(window, "windowDurationMins");
        var resetsAt = ReadNumber(window, "resetsAt");
        Console.WriteLine($"  {label}: used={usedPercent:0.##}% duration={durationMinutes:0}m resetsAt={resetsAt:0}");
    }

    private static void PrintClaudeUsage(JsonElement root)
    {
        Console.WriteLine("Claude: Ready");

        var printed = false;
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!property.Value.TryGetProperty("utilization", out var utilization) || utilization.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var reset = property.Value.TryGetProperty("resets_at", out var resetsAt)
                ? FormatSafeJsonValue(resetsAt)
                : "unknown";
            Console.WriteLine($"  {property.Name}: used={utilization.GetDouble():0.##}% resetsAt={reset}");
            printed = true;
        }

        if (!printed)
        {
            Console.WriteLine("  Usage schema: unrecognized");
        }
    }

    private static string FormatSafeJsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "unknown",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Null => "null",
        _ => "unknown"
    };

    private static double ReadNumber(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var number)
            ? number
            : 0;

    private static bool TryGetError(JsonElement root, out string error)
    {
        error = string.Empty;
        if (!root.TryGetProperty("error", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        error = value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString() ?? "Unknown"
            : "Unknown";
        return true;
    }
}
