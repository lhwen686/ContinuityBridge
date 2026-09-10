using System.Diagnostics;
namespace ContinuityBridge.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0) return;
        using var instance = new Mutex(true, @"Local\ContinuityBridge.Product.v1", out bool created);
        if (!created)
        {
            MessageBox.Show("ContinuityBridge 已在当前会话运行，请从通知区域打开。", "ContinuityBridge");
            return;
        }
        ApplicationConfiguration.Initialize();
        using var context = new TrayContext();
        Application.Run(context);
        instance.ReleaseMutex();
    }
}
