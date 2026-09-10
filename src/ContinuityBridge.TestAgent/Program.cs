using System.Diagnostics;
namespace ContinuityBridge.TestAgent;
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0) return;
        using var instance = new Mutex(true, @"Local\ContinuityBridge.Qa.Desktop", out bool created);
        if (!created) { MessageBox.Show("当前桌面已有测试会话。", "ContinuityBridge TestAgent"); return; }
        ApplicationConfiguration.Initialize();
        Application.Run(new RunnerForm());
        instance.ReleaseMutex();
    }
}
