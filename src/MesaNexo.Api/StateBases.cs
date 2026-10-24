using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Api;
// What each session last received from /api/state, so the next poll can carry only the differences.
// Two bases per session (a reply may be lost while the next poll is in flight), dropped after 30 idle minutes.
// With `keepStates` (diagnostics) the whole state is kept too, so every delta can be checked against it.
public sealed class StateBases(bool keepStates = false) {
    sealed class Entry { public readonly LinkedList<(string Id, StateFingerprint Fingerprint, JsonObject? State)> Recent = new(); public DateTimeOffset Seen; }
    readonly ConcurrentDictionary<string, Entry> sessions = new(); long calls;
    public (StateFingerprint Fingerprint, JsonObject? State)? Find(string session, string id) {

        if (id == "" || !sessions.TryGetValue(session, out var entry)) return null;
        lock (entry) { foreach (var x in entry.Recent) if (x.Id == id) return (x.Fingerprint, x.State); }
        return null;

    }
    public string Remember(string session, StateFingerprint fingerprint, JsonObject? state) {

        var id = J.Id();
        var entry = sessions.GetOrAdd(session, _ => new());
        lock (entry) { entry.Recent.AddFirst((id, fingerprint, keepStates ? state : null)); while (entry.Recent.Count > 2) entry.Recent.RemoveLast(); entry.Seen = DateTimeOffset.UtcNow; }
        if (Interlocked.Increment(ref calls) % 200 == 0)
        foreach (var (key, value) in sessions)
        if (DateTimeOffset.UtcNow - value.Seen > TimeSpan.FromMinutes(30)) sessions.TryRemove(key, out _);
        return id;

    }
}
