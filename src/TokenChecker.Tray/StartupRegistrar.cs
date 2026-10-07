using Microsoft.Win32;

namespace TokenChecker.Tray;

internal static class StartupRegistrar
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TokenChecker";

    public static bool IsEnabled()
    {
        var executablePath = GetExecutablePath();
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        return string.Equals(value, Quote(executablePath), StringComparison.OrdinalIgnoreCase);
    }

    public static void Enable()
    {
        var executablePath = GetExecutablePath();
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("시작 프로그램 레지스트리 키를 열 수 없습니다.");
        key.SetValue(ValueName, Quote(executablePath), RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string GetExecutablePath() =>
        Environment.ProcessPath
        ?? throw new InvalidOperationException("현재 실행 파일 경로를 확인할 수 없습니다.");

    private static string Quote(string path) => $"\"{path}\"";
}
