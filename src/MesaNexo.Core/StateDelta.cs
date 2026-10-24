using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
namespace MesaNexo.Core;
// What a client already holds of /api/state: per top-level key a hash, and for arrays of objects with unique ids
// their order and one hash per item. The server keeps it per session so the next response carries only differences.
public sealed record StateFingerprint(IReadOnlyDictionary<string, ulong> Values, IReadOnlyDictionary<string, (string[] Ids, Dictionary<string, ulong> Items)> Collections);
// One element of a state collection, built once and shared: Node is never mutated nor attached to another node.
public sealed record StateItem(string Id, JsonObject Node, ulong Hash);
// /api/state as ordered top-level entries. A collection holds shared StateItems (with their hashes already computed),
// any other entry a node of its own. Materialize builds the JSON object a caller may keep and modify.
public sealed class StateParts {
    readonly List<string> order = []; readonly Dictionary<string, object?> entries = [];
    public JsonNode? this[string key] { get => entries.TryGetValue(key, out var v) ? v as JsonNode : null; set => Put(key, value); }
    public void Items(string key, IEnumerable<StateItem> items) => Put(key, items.ToArray());
    void Put(string key, object? value) { if (!entries.ContainsKey(key)) order.Add(key); entries[key] = value; }
    public bool Remove(string key) { order.Remove(key); return entries.Remove(key); }
    public IReadOnlyList<string> Keys => order;
    public string S(string key) => this[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    internal object? Raw(string key) => entries[key];
    public JsonObject Materialize() {

        var o = new JsonObject();
        foreach (var key in order) o[key] = entries[key] is StateItem[] items ? new JsonArray(items.Select(x => (JsonNode)x.Node.DeepClone()).ToArray()) : (entries[key] as JsonNode)?.DeepClone();
        return o;

    }
    public static StateParts From(JsonObject state) { var p = new StateParts(); foreach (var (key, value) in state) p.Put(key, value); return p; }
}
public static class StateDelta {
    public static ulong Hash(JsonNode? node) => BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(node?.ToJsonString() ?? "null")));
    // A collection is an array of objects with unique, non-empty ids; anything else is compared as a whole value.
    static StateItem[]? Collection(object? value) {

        StateItem[] items;
        if (value is StateItem[] shared) items = shared;
        else if (value is JsonArray array) { items = new StateItem[array.Count]; for (var i = 0; i < array.Count; i++) { if (array[i] is not JsonObject o || o["id"] is not JsonValue v || !v.TryGetValue<string>(out var id)) return null; items[i] = new(id, o, Hash(o)); } } else return null;
        var seen = new HashSet<string>();
        return items.All(x => x.Id != "" && seen.Add(x.Id)) ? items : null;

    }
    public static StateFingerprint Fingerprint(JsonObject state) => Fingerprint(StateParts.From(state));
    public static StateFingerprint Fingerprint(StateParts state) {

        var values = new Dictionary<string, ulong>();
        var collections = new Dictionary<string, (string[], Dictionary<string, ulong>)>();
        foreach (var key in state.Keys) {

            var raw = state.Raw(key);
            if (Collection(raw) is { } items) collections[key] = (items.Select(x => x.Id).ToArray(), items.ToDictionary(x => x.Id, x => x.Hash));
            else values[key] = Hash(raw as JsonNode);

        }
        return new(values, collections);

    }
    public static JsonObject Diff(StateFingerprint before, JsonObject state, StateFingerprint after) => Diff(before, StateParts.From(state), after);
    // Collections travel item by item; any other value travels whole when it changes.
    // The client rebuilds an array as: its old order without removed ids, then new ids in arrival order;
    // "order" is sent only when that rebuild would be wrong.
    public static JsonObject Diff(StateFingerprint before, StateParts state, StateFingerprint after) {

        var set = new JsonObject();
        var unset = new JsonArray();
        var collections = new JsonObject();
        foreach (var key in state.Keys) {

            var raw = state.Raw(key);
            if (after.Collections.TryGetValue(key, out var now) && before.Collections.TryGetValue(key, out var old)) {

                var items = Collection(raw)!;
                var upsert = new JsonArray();
                foreach (var item in items)
                if (!old.Items.TryGetValue(item.Id, out var h) || h != item.Hash) upsert.Add(item.Node.DeepClone());
                var remove = old.Ids.Where(id => !now.Items.ContainsKey(id)).ToArray();
                var rebuilt = old.Ids.Where(now.Items.ContainsKey).Concat(now.Ids.Where(id => !old.Items.ContainsKey(id)));
                var entry = new JsonObject();
                if (upsert.Count > 0) entry["upsert"] = upsert;
                if (remove.Length > 0) entry["remove"] = J.N(remove);
                if (!rebuilt.SequenceEqual(now.Ids)) entry["order"] = J.N(now.Ids);
                if (entry.Count > 0) collections[key] = entry;

            } else if (!before.Values.TryGetValue(key, out var h) || !after.Values.TryGetValue(key, out var n) || h != n) set[key] = raw is StateItem[] shared ? new JsonArray(shared.Select(x => (JsonNode)x.Node.DeepClone()).ToArray()) : (raw as JsonNode)?.DeepClone();

        }
        foreach (var key in before.Values.Keys.Concat(before.Collections.Keys))
        if (!state.Keys.Contains(key)) unset.Add(key);
        return J.O("delta", true, "set", set, "unset", unset, "collections", collections);

    }
    // Applies a Diff to the state it was computed against, in place.
    public static JsonObject Apply(JsonObject current, JsonObject delta) {

        foreach (var key in delta.A("unset").Strings()) current.Remove(key);
        foreach (var (key, value) in delta.Obj("set"))
            current[key] = value?.DeepClone();
        foreach (var (key, node) in delta.Obj("collections")) {

            var change = (JsonObject)node!;
            var array = current[key] as JsonArray ?? throw new InvalidOperationException("Delta sobre una colección inexistente: " + key);
            var items = array.OfType<JsonObject>().ToDictionary(x => x.S("id"));
            var oldOrder = items.Keys.ToArray();
            array.Clear();
            var added = new List<string>();
            foreach (var item in change.A("upsert").Objects()) { var id = item.S("id"); if (!items.ContainsKey(id)) added.Add(id); items[id] = (JsonObject)item.DeepClone(); }
            var removed = change.A("remove").Strings().ToHashSet();
            var order = change["order"] is JsonArray explicitOrder ? explicitOrder.Strings() : oldOrder.Where(id => !removed.Contains(id)).Concat(added).ToArray();
            foreach (var id in order) array.Add(items[id]);

        }
        return current;

    }
}
