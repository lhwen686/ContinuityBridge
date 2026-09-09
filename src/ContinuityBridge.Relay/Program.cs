using ContinuityBridge.Relay;

if (args.Length == 2 && args[0] == "--provision")
{
    DeviceRegistry.Provision(args[1]);
    return;
}
await RelayHost.Build(args).RunAsync().ConfigureAwait(false);
