namespace ContinuityBridge.Windows.Tests;

internal static class StaTestThread
{
    internal static Task<T> RunAsync<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(
            () =>
            {
                try
                {
                    completion.TrySetResult(action());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            })
        {
            IsBackground = true,
            Name = "ContinuityBridge.Tests.ClipboardSTA",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal static Task RunAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunAsync(
            () =>
            {
                action();
                return true;
            });
    }
}
