using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
namespace MesaNexo.Core;
// What a client already holds of /api/state: per top-level key a hash, and for arrays of objects with unique ids
// their order and one hash per item. The server keeps it per session so the next response carries only differences.
public sealed record StateFingerprint(IReadOnlyDictionary<string,ulong> Values,IReadOnlyDictionary<string,(string[] Ids,Dictionary<string,ulong> Items)> Collections);
public static class StateDelta
{
    static ulong Hash(JsonNode? node)=>BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(node?.ToJsonString()??"null")));
    static string[]? Ids(JsonNode? node)
    {
        if(node is not JsonArray array)return null;var ids=new string[array.Count];var seen=new HashSet<string>();
        for(var i=0;i<array.Count;i++){if(array[i] is not JsonObject o||o["id"] is not JsonValue v||!v.TryGetValue<string>(out var id)||id==""||!seen.Add(id))return null;ids[i]=id;}
        return ids;
    }
    public static StateFingerprint Fingerprint(JsonObject state)
    {
        var values=new Dictionary<string,ulong>();var collections=new Dictionary<string,(string[],Dictionary<string,ulong>)>();
        foreach(var (key,value) in state){
            if(Ids(value) is {} ids){var items=new Dictionary<string,ulong>(ids.Length);var array=(JsonArray)value!;for(var i=0;i<ids.Length;i++)items[ids[i]]=Hash(array[i]);collections[key]=(ids,items);}
            else values[key]=Hash(value);
        }
        return new(values,collections);
    }
    // Arrays of objects with unique ids travel item by item; any other value travels whole when it changes.
    // The client rebuilds an array as: its old order without removed ids, then new ids in arrival order;
    // "order" is sent only when that rebuild would be wrong.
    public static JsonObject Diff(StateFingerprint before,JsonObject state,StateFingerprint after)
    {
        var set=new JsonObject();var unset=new JsonArray();var collections=new JsonObject();
        foreach(var (key,value) in state){
            if(after.Collections.TryGetValue(key,out var now)&&before.Collections.TryGetValue(key,out var old)){
                var array=(JsonArray)value!;var upsert=new JsonArray();
                for(var i=0;i<now.Ids.Length;i++)if(!old.Items.TryGetValue(now.Ids[i],out var h)||h!=now.Items[now.Ids[i]])upsert.Add(array[i]!.DeepClone());
                var remove=old.Ids.Where(id=>!now.Items.ContainsKey(id)).ToArray();
                var rebuilt=old.Ids.Where(now.Items.ContainsKey).Concat(now.Ids.Where(id=>!old.Items.ContainsKey(id)));
                var entry=new JsonObject();if(upsert.Count>0)entry["upsert"]=upsert;if(remove.Length>0)entry["remove"]=J.N(remove);if(!rebuilt.SequenceEqual(now.Ids))entry["order"]=J.N(now.Ids);
                if(entry.Count>0)collections[key]=entry;
            }
            else if(!before.Values.TryGetValue(key,out var h)||!after.Values.TryGetValue(key,out var n)||h!=n)set[key]=value?.DeepClone();
        }
        foreach(var key in before.Values.Keys.Concat(before.Collections.Keys))if(!state.ContainsKey(key))unset.Add(key);
        return J.O("delta",true,"set",set,"unset",unset,"collections",collections);
    }
    // Applies a Diff to the state it was computed against, in place.
    public static JsonObject Apply(JsonObject current,JsonObject delta)
    {
        foreach(var key in delta.A("unset").Strings())current.Remove(key);
        foreach(var (key,value) in delta.Obj("set"))current[key]=value?.DeepClone();
        foreach(var (key,node) in delta.Obj("collections")){
            var change=(JsonObject)node!;var array=current[key] as JsonArray??throw new InvalidOperationException("Delta sobre una colección inexistente: "+key);
            var items=array.OfType<JsonObject>().ToDictionary(x=>x.S("id"));var oldOrder=items.Keys.ToArray();array.Clear();
            var added=new List<string>();
            foreach(var item in change.A("upsert").Objects()){var id=item.S("id");if(!items.ContainsKey(id))added.Add(id);items[id]=(JsonObject)item.DeepClone();}
            var removed=change.A("remove").Strings().ToHashSet();
            var order=change["order"] is JsonArray explicitOrder?explicitOrder.Strings():oldOrder.Where(id=>!removed.Contains(id)).Concat(added).ToArray();
            foreach(var id in order)array.Add(items[id]);
        }
        return current;
    }
}
