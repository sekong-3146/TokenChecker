using System.Threading;
using System.Windows.Forms;

namespace TokenChecker.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\TokenChecker.SingleInstance",
            createdNew: out var isNewInstance);
        if (!isNewInstance)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
