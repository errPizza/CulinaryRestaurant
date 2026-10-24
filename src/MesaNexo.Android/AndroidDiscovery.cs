using Android.Content;
using Android.Net.Nsd;
using MesaNexo.Client;
namespace MesaNexo.Android;

public sealed class AndroidDiscovery(Context context): IDiscoveryProvider {
    public async Task<IReadOnlyList<DiscoveryCandidate>> Browse(CancellationToken ct = default) {

        var manager = (NsdManager)context.GetSystemService(Context.NsdService)!;
        var listener = new Listener(manager);
        manager.DiscoverServices("_culinaryos._tcp.", NsdProtocol.DnsSd, listener);
        try { await Task.Delay(5000, ct); } finally { manager.StopServiceDiscovery(listener); }
        lock (listener.Candidates) return listener.Candidates.ToArray();

    }
    sealed class Listener(NsdManager manager): Java.Lang.Object, NsdManager.IDiscoveryListener {
        public readonly List<DiscoveryCandidate> Candidates = [];
        public void OnDiscoveryStarted(string? type) { }
        public void OnDiscoveryStopped(string? type) { }
        public void OnStartDiscoveryFailed(string? type, NsdFailure error) { }
        public void OnStopDiscoveryFailed(string? type, NsdFailure error) { }
        public void OnServiceFound(NsdServiceInfo? info) { if (info is not null) manager.ResolveService(info, new Resolver(Candidates)); }
        public void OnServiceLost(NsdServiceInfo? info) { }
    }
    sealed class Resolver(List<DiscoveryCandidate> candidates): Java.Lang.Object, NsdManager.IResolveListener {
        public void OnResolveFailed(NsdServiceInfo? info, NsdFailure error) { }
        public void OnServiceResolved(NsdServiceInfo? info) { if (info?.Host?.HostAddress is string address) lock (candidates) candidates.Add(new(address, info.Port, info.ServiceName ?? address)); }
    }
}