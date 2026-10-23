using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
namespace MesaNexo.Core;
public static class J
{
    public static JsonObject O(params object?[] pairs) { var o=new JsonObject();for(int i=0;i<pairs.Length;i+=2)o[(string)pairs[i]!]=N(pairs[i+1]);return o; }
    public static JsonNode? N(object? value)=>value is JsonNode n?n.DeepClone():JsonSerializer.SerializeToNode(value);
    public static string S(this JsonNode? n,string key,string fallback="")=>n?[key]?.GetValue<string>()??fallback;
    public static long L(this JsonNode? n,string key,long fallback=0)=>n?[key]?.GetValue<long>()??fallback;
    public static int I(this JsonNode? n,string key,int fallback=0)=>checked((int)n.L(key,fallback));
    public static bool B(this JsonNode? n,string key,bool fallback=false)=>n?[key]?.GetValue<bool>()??fallback;
    public static JsonArray A(this JsonNode? n,string key)=>n?[key] as JsonArray??[];
    public static JsonObject Obj(this JsonNode? n,string key)=>n?[key] as JsonObject??new();
    public static IEnumerable<JsonObject> Objects(this JsonArray a)=>a.OfType<JsonObject>();
    public static string[] Strings(this JsonArray a)=>a.Select(x=>x!.GetValue<string>()).ToArray();
    public static string Id()=>Guid.NewGuid().ToString("N");
    public static string Now()=>DateTimeOffset.UtcNow.ToString("O");
    public static string Hash(string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Canonical(JsonNode? node)=>Sort(node)?.ToJsonString(new JsonSerializerOptions{WriteIndented=false})??"null";
    static JsonNode? Sort(JsonNode? n)=>n switch {JsonObject o=>new JsonObject(o.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>new KeyValuePair<string,JsonNode?>(p.Key,Sort(p.Value)))),JsonArray a=>new JsonArray(a.Select(Sort).ToArray()),_=>n?.DeepClone()};
}
public sealed class BusinessException(int status,string message):Exception(message){public int Status=>status;}
public static class Rules
{
    public static void Need(bool ok,string message,int status=400){if(!ok)throw new BusinessException(status,message);}
    public static string Text(JsonNode p,string key,int max=1000){var s=p.S(key).Trim();Need(s.Length>0&&s.Length<=max,$"{key}: texto requerido (máximo {max})");return s;}
    public static long Amount(JsonNode p,string key,long min=0,long max=100_000_000){long n;try{n=p.L(key);}catch{throw new BusinessException(400,$"{key}: usa un importe entero en centavos");}Need(n>=min&&n<=max,$"{key}: importe fuera de rango");return n;}
    public static string Choice(JsonNode p,string key,params string[] choices){var s=p.S(key);Need(choices.Contains(s),$"{key}: valor inválido");return s;}
    public static void Role(JsonObject u,params string[] roles)=>Need(roles.Contains(u.S("role")),"Tu rol no autoriza esta operación",403);
}
public record StoredRecord(string RecordId,long Version,JsonObject Data);
// The committed records of a branch at one meta version. Shared by every request, so nothing in it is ever mutated:
// a commit produces a new CommittedState over immutable collections, and field indexes carry over updated in place
// of being rebuilt, so a commit costs in proportion to what it changed, not to the size of the branch.
public sealed class CommittedState
{
    readonly ImmutableDictionary<string,StoredRecord> records;readonly ImmutableDictionary<string,ImmutableList<string>> kinds;
    readonly System.Collections.Concurrent.ConcurrentDictionary<(string Kind,string Field),ImmutableDictionary<string,ImmutableList<string>>> indexes;
    public static readonly CommittedState Empty=new([],0);
    public long Version{get;}
    public int Count=>records.Count;
    public IReadOnlyDictionary<string,StoredRecord> Records=>records;
    public CommittedState(IEnumerable<StoredRecord> ordered,long version)
    {
        var r=ImmutableDictionary.CreateBuilder<string,StoredRecord>();var k=new Dictionary<string,ImmutableList<string>.Builder>();
        foreach(var x in ordered){r.Add(x.RecordId,x);var kind=x.Data.S("kind");if(!k.TryGetValue(kind,out var keys))k[kind]=keys=ImmutableList.CreateBuilder<string>();keys.Add(x.RecordId);}
        records=r.ToImmutable();kinds=k.ToImmutableDictionary(x=>x.Key,x=>x.Value.ToImmutable());indexes=new();Version=version;
    }
    CommittedState(ImmutableDictionary<string,StoredRecord> records,ImmutableDictionary<string,ImmutableList<string>> kinds,System.Collections.Concurrent.ConcurrentDictionary<(string,string),ImmutableDictionary<string,ImmutableList<string>>> indexes,long version){this.records=records;this.kinds=kinds;this.indexes=indexes;Version=version;}
    public bool TryGet(string key,out StoredRecord record)=>records.TryGetValue(key,out record!);
    public IReadOnlyList<string> Keys(string kind)=>kinds.TryGetValue(kind,out var keys)?keys:[];
    // Keys of committed records whose field has this value (missing or null counts as "", as J.S reads it).
    public IReadOnlyList<string> KeysWhere(string kind,string field,string value)=>indexes.GetOrAdd((kind,field),x=>Keys(x.Kind).GroupBy(k=>Field(records[k].Data,x.Field)).ToImmutableDictionary(g=>g.Key,g=>g.ToImmutableList())).TryGetValue(value,out var keys)?keys:[];
    public static string Field(JsonObject data,string field)=>data[field] switch{null=>"",JsonValue v when v.TryGetValue<string>(out var s)=>s,var n=>n.ToJsonString()};
    // Kinds in `bounded` keep only their newest records in memory; the store still holds every one of them.
    public CommittedState With(IEnumerable<StoredRecord> changed,long version,IReadOnlyDictionary<string,int> bounded)
    {
        var r=records.ToBuilder();var k=kinds.ToBuilder();var moved=new List<(string Key,StoredRecord? Before,StoredRecord? After)>();
        foreach(var c in changed){
            records.TryGetValue(c.RecordId,out var before);if(before is null){var kind=c.Data.S("kind");k[kind]=(k.TryGetValue(kind,out var keys)?keys:[]).Add(c.RecordId);}
            r[c.RecordId]=c;moved.Add((c.RecordId,before,c));
        }
        foreach(var (kind,limit) in bounded)if(k.TryGetValue(kind,out var keys)&&keys.Count>limit){foreach(var key in keys.Take(keys.Count-limit)){moved.Add((key,r[key],null));r.Remove(key);}k[kind]=keys.RemoveRange(0,keys.Count-limit);}
        var carried=new System.Collections.Concurrent.ConcurrentDictionary<(string,string),ImmutableDictionary<string,ImmutableList<string>>>();
        foreach(var ((kind,field),index) in indexes){
            var groups=index;
            foreach(var (key,before,after) in moved){
                var old=before is not null&&before.Data.S("kind")==kind?Field(before.Data,field):null;var now=after is not null&&after.Data.S("kind")==kind?Field(after.Data,field):null;
                if(old==now)continue;
                if(old is not null&&groups.TryGetValue(old,out var from)){var rest=from.Remove(key);groups=rest.IsEmpty?groups.Remove(old):groups.SetItem(old,rest);}
                if(now is not null)groups=groups.SetItem(now,(groups.TryGetValue(now,out var list)?list:[]).Add(key));
            }
            carried[(kind,field)]=groups;
        }
        return new(r.ToImmutable(),k.ToImmutable(),carried,version);
    }
}
public interface IBranchStore
{
    Task<JsonNode?> CallAsync(JsonObject request,CancellationToken ct=default);
}
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string encoded,string password);
}
// One request's view of the branch over a CommittedState. A read-only view hands out the committed objects
// themselves; a writable state clones each record the first time it is reached, so only what an operation
// touched is compared when computing its changes.
public sealed class BranchState(CommittedState committed,bool readOnly=false)
{
    readonly Dictionary<string,JsonObject> touched=[];readonly Dictionary<string,List<string>> added=[];
    public BranchState():this(CommittedState.Empty){}
    public CommittedState Committed=>committed;
    public bool ReadOnly=>readOnly;
    public int Touched=>touched.Count;
    public IReadOnlyDictionary<string,StoredRecord> Original=>committed.Records;
    public int Count=>committed.Count+added.Values.Sum(x=>x.Count);
    public bool Contains(string key)=>touched.ContainsKey(key)||committed.TryGet(key,out _);
    JsonObject? Resolve(string key)
    {
        if(touched.TryGetValue(key,out var value))return value;
        if(!committed.TryGet(key,out var stored))return null;
        if(readOnly)return stored.Data;
        value=(JsonObject)stored.Data.DeepClone();touched[key]=value;return value;
    }
    IEnumerable<JsonObject> All(string kind)
    {
        foreach(var key in committed.Keys(kind))yield return Resolve(key)!;
        if(added.TryGetValue(kind,out var keys))foreach(var key in keys.ToArray())yield return touched[key];
    }
    public IEnumerable<JsonObject> List(string kind)=>All(kind).Where(x=>x.S("kind")==kind&&!x.B("deleted"));
    // Same records as List(kind).Where(x=>x.S(field)==value), without reaching (or cloning) the rest of the kind.
    public IEnumerable<JsonObject> Related(string kind,string field,string value)
    {
        IEnumerable<string> keys=committed.KeysWhere(kind,field,value);
        if(!readOnly)keys=keys.Concat(touched.Where(x=>x.Value.S("kind")==kind).Select(x=>x.Key)).Distinct();
        return keys.Select(Resolve).Where(x=>x is not null&&x.S("kind")==kind&&!x.B("deleted")&&CommittedState.Field(x,field)==value).Select(x=>x!);
    }
    // The newest `count` records of a kind matching `where`, in insertion order, reaching only those.
    public IEnumerable<JsonObject> Latest(string kind,int count,Func<JsonObject,bool>? where=null)
    {
        var found=new List<JsonObject>();
        var newKeys=added.TryGetValue(kind,out var a)?a:[];var oldKeys=committed.Keys(kind);
        for(var i=newKeys.Count+oldKeys.Count-1;i>=0&&found.Count<count;i--){var x=i>=oldKeys.Count?touched[newKeys[i-oldKeys.Count]]:Resolve(oldKeys[i])!;if(x.S("kind")==kind&&!x.B("deleted")&&(where is null||where(x)))found.Add(x);}
        found.Reverse();return found;
    }
    public JsonObject Get(string kind,string id)=>Resolve(Key(kind,id)) is {} value&&!value.B("deleted")?value:throw new BusinessException(404,"Registro no encontrado");
    public JsonObject Add(string kind,JsonObject data)
    {
        if(readOnly)throw new InvalidOperationException("Vista de sólo lectura");data["kind"]=kind;if(data.S("id")=="")data["id"]=J.Id();var key=Key(kind,data.S("id"));
        if(Contains(key))throw new ArgumentException("An item with the same key has already been added. Key: "+key);
        touched[key]=data;if(!added.TryGetValue(kind,out var keys))added[kind]=keys=[];keys.Add(key);return data;
    }
    // Inserts or replaces a whole record under its key; data must already carry its kind.
    public void Put(string key,JsonObject data)
    {
        if(readOnly)throw new InvalidOperationException("Vista de sólo lectura");var kind=data.S("kind");
        if(!Contains(key)){if(!added.TryGetValue(kind,out var keys))added[kind]=keys=[];keys.Add(key);}touched[key]=data;
    }
    public static string Key(string kind,string id)=>"mn_"+kind+"_"+id;
    public JsonObject Meta=>Get("meta","site");
    // DeepEquals is semantic (property order, number spelling): equal data is never written, different data always is.
    public IEnumerable<JsonObject> Changes()=>touched.Where(pair=>!committed.TryGet(pair.Key,out var prior)||!JsonNode.DeepEquals(prior.Data,pair.Value)).Select(pair=>J.O("record_id",pair.Key,"expected_version",committed.TryGet(pair.Key,out var p)?p.Version:0,"data",pair.Value));
}