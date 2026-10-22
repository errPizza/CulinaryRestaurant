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
public interface IBranchStore
{
    Task<JsonNode?> CallAsync(JsonObject request,CancellationToken ct=default);
}
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string encoded,string password);
}
public sealed class BranchState
{
    public Dictionary<string,StoredRecord> Original {get;}=[];
    public Dictionary<string,JsonObject> Records {get;}=[];
    public IEnumerable<JsonObject> List(string kind)=>Records.Values.Where(x=>x.S("kind")==kind&&!x.B("deleted"));
    public JsonObject Get(string kind,string id)=>Records.TryGetValue(Key(kind,id),out var value)&&!value.B("deleted")?value:throw new BusinessException(404,"Registro no encontrado");
    public JsonObject Add(string kind,JsonObject data){data["kind"]=kind;if(data.S("id")=="")data["id"]=J.Id();Records.Add(Key(kind,data.S("id")),data);return data;}
    public static string Key(string kind,string id)=>"mn_"+kind+"_"+id;
    public JsonObject Meta=>Get("meta","site");
    public IEnumerable<JsonObject> Changes()=>Records.Where(pair=>!Original.TryGetValue(pair.Key,out var prior)||J.Canonical(prior.Data)!=J.Canonical(pair.Value)).Select(pair=>J.O("record_id",pair.Key,"expected_version",Original.TryGetValue(pair.Key,out var p)?p.Version:0,"data",pair.Value));
}