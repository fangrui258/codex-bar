using System.Runtime.InteropServices;

namespace CodexBar;

internal static class Program
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is [AntigravityStatusBridge.BridgeArgument])
            return AntigravityStatusBridge.RunFromStandardInput();

        _ = SetCurrentProcessExplicitAppUserModelID("CodexBar.Desktop");
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "Local\\CodexBar.SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("CodexBar is already running in the notification area.", "CodexBar",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.Run(new WidgetForm());
        GC.KeepAlive(mutex);
        return 0;
    }
}
