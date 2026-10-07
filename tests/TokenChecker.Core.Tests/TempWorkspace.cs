namespace TokenChecker.Core.Tests;

internal sealed class TempWorkspace : IDisposable
{
    private TempWorkspace(string directoryPath)
    {
        DirectoryPath = directoryPath;
    }

    public string DirectoryPath { get; }

    public static TempWorkspace Create()
    {
        var directoryPath = Path.Combine(
            Path.GetTempPath(),
            "tokenchecker-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        return new TempWorkspace(directoryPath);
    }

    public string GetPath(string fileName) => Path.Combine(DirectoryPath, fileName);

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }

        var parentPath = Directory.GetParent(DirectoryPath)?.FullName;
        if (!string.IsNullOrEmpty(parentPath))
        {
            try
            {
                Directory.Delete(parentPath);
            }
            catch (IOException)
            {
                // Another test workspace may still be using the shared parent.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup must not hide the test result when the parent is unavailable.
            }
        }

        GC.SuppressFinalize(this);
    }
}
