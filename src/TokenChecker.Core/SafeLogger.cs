using System.Globalization;
using System.Text;

namespace TokenChecker.Core;

public sealed class SafeLogger
{
    private const int MaxBytes = 256 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly object Sync = new();

    public SafeLogger(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TokenChecker",
            "diagnostics.log");
    }

    public string Path { get; }

    public void LogProviderFailure(string providerId, ProviderStatus status, int? httpStatusCode, DateTimeOffset at)
    {
        var safeProviderId = (providerId ?? string.Empty)
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        var code = httpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "-";
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{at:O}\t{safeProviderId}\t{status}\t{code}{Environment.NewLine}");

        try
        {
            lock (Sync)
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                var directoryPath = string.IsNullOrEmpty(directory) ? "." : directory;
                Directory.CreateDirectory(directoryPath);

                var lineBytes = Utf8.GetBytes(line);
                var existingBytes = File.Exists(Path) ? new FileInfo(Path).Length : 0L;
                if (existingBytes + lineBytes.Length > MaxBytes)
                {
                    File.WriteAllBytes(Path, lineBytes);
                }
                else
                {
                    File.AppendAllText(Path, line, Utf8);
                }
            }
        }
        catch
        {
            // Diagnostics must never interfere with usage refreshes.
        }
    }
}
