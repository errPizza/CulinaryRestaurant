using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
// Credentials of the live integrations, one file per provider in /etc/culinaryos/integrations, owned by root:culinaryos-app with
// mode 0640 (examples in deploy/integrations). A missing file leaves that provider without credentials; a broken one is reported
// on its card and never stops the backend. Secrets stay in these files: neither the branch store nor the clients ever see them.
public sealed class IntegrationConfig
{
    public const string Directory="/etc/culinaryos/integrations";
    readonly Dictionary<string,JsonObject> files=[];
    public readonly ConcurrentDictionary<string,string> Status=new(Restaurant.IntegrationKeys.Select(k=>KeyValuePair.Create(k,"sin credenciales")));
    public static IntegrationConfig Load(string? directory)
    {
        var config=new IntegrationConfig();if(directory is null||!System.IO.Directory.Exists(directory))return config;
        foreach(var key in Restaurant.IntegrationKeys){
            var path=Path.Combine(directory,key+".json");if(!System.IO.File.Exists(path))continue;
            try{config.files[key]=JsonNode.Parse(System.IO.File.ReadAllText(path)) as JsonObject??throw new BusinessException(400,"no es un objeto JSON");}
            catch(Exception e){config.Status[key]="error: "+(e is BusinessException?e.Message:"archivo ilegible o JSON inválido");}
        }
        return config;
    }
    // A file given on the command line, or the one a provider used before this directory existed (/etc/culinaryos/fiscal.json,
    // printer.json): an explicit file wins, a legacy one only fills a provider without its own.
    public IntegrationConfig Adopt(string key,string? path,bool explicitFile)
    {
        if(path is null||!System.IO.File.Exists(path)||!explicitFile&&(files.ContainsKey(key)||Status[key]!="sin credenciales"))return this;
        try{files[key]=JsonNode.Parse(System.IO.File.ReadAllText(path)) as JsonObject??throw new BusinessException(400,"no es un objeto JSON");Status[key]="sin credenciales";}
        catch(Exception e){files.Remove(key);Status[key]="error: "+(e is BusinessException?e.Message:"archivo ilegible o JSON inválido");}
        return this;
    }
    public JsonObject? File(string key)=>files.GetValueOrDefault(key);
    // Builds a provider's service from its file; a validation error marks the provider instead of failing the startup.
    public T? Use<T>(string key,Func<JsonObject,T> build)where T:class
    {
        if(File(key) is not {} file)return null;
        try{var built=build(file);Status[key]="lista";return built;}
        catch(Exception e){Status[key]="error: "+(e is BusinessException?e.Message:"configuración inválida");return null;}
    }
    public void Ready(string key)=>Status[key]="lista";
    public string[] ReadyKeys=>Status.Where(x=>x.Value=="lista").Select(x=>x.Key).Order(StringComparer.Ordinal).ToArray();
}
// What a provider answered: the status and fields to store on the record, and a line for the integration log.
public sealed record JobResult(string Status,JsonObject Fields,string Summary="",string Reference="")
{
    public static JobResult Of(string status,string summary="",string reference="",params (string Key,object? Value)[] fields)=>new(status,new JsonObject(fields.Select(f=>KeyValuePair.Create(f.Key,J.N(f.Value)))),summary,reference);
}
public sealed partial class BranchService
{
    public IntegrationConfig Integrations{get;private set;}=new();
    // The server's credentials, given to the domain as a catalog without secrets.
    public void UseIntegrations(IntegrationConfig config,JsonArray? terminals=null){Integrations=config;Domain.LiveCatalog=J.O("providers",J.N(config.ReadyKeys),"terminals",terminals??new JsonArray());}
    public void NoteIntegration(string key,string state){if(Integrations.Status.ContainsKey(key))Integrations.Status[key]=state;}
    JsonObject IntegrationConnection()=>new(Integrations.Status.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>KeyValuePair.Create(x.Key,(JsonNode?)x.Value)));
    static bool Leased(JsonObject x,DateTimeOffset now)=>DateTimeOffset.TryParse(x.S("leaseUntil"),out var lease)&&lease>now;
    // Outside work kept durable. The oldest queued record is claimed (status "sending", an attempt id and a lease) in its own
    // commit before the provider is called once; the answer is stored after. A claim whose lease ran out without an answer is
    // never sent again: `recover` asks the provider what happened, and without an answer it stays "uncertain" for a person.
    // Each kind of call on a record claims it with its own status (`claim`), so a lost claim goes back to the right `recover`.
    public async Task<bool> SendNext(string kind,string actor,Func<JsonObject,bool> queued,Func<BranchState,JsonObject,CancellationToken,Task<JobResult>> send,Func<BranchState,JsonObject,CancellationToken,Task<JobResult?>>? recover=null,CancellationToken ct=default,string claim="sending")
    {
        var s=await Load(ct);var now=DateTimeOffset.UtcNow;
        var lost=s.List(kind).FirstOrDefault(x=>x.S("status")==claim&&!Leased(x,now));
        if(lost is not null){
            var id=lost.S("id");JobResult? found=null;try{if(recover is not null)found=await recover(s,(JsonObject)lost.DeepClone(),ct);}catch(Exception)when(!ct.IsCancellationRequested){}
            found??=JobResult.Of("uncertain","Sin respuesta del proveedor; revisa en su portal antes de repetir",lost.S("reference"));
            return await Record(kind,id,lost.S("attemptId"),actor,found,ct);
        }
        var row=s.List(kind).Where(x=>queued(x)&&!Leased(x,now)).OrderBy(x=>x.S("at"),StringComparer.Ordinal).FirstOrDefault();if(row is null)return false;
        var rid=row.S("id");var attempt=J.Id();row["status"]=claim;row["attemptId"]=attempt;row["leaseUntil"]=now.AddMinutes(2).ToString("O");row["attempts"]=row.L("attempts")+1;
        var snapshot=(JsonObject)row.DeepClone();var view=s;
        try{await Commit(s,J.Id(),actor,J.Hash(rid+attempt),J.O("id",rid,"attemptId",attempt),kind+".claim",ct);}catch(StoreException e)when(e.Code=="COS-DATA-CONFLICT"){return false;}
        JobResult result;try{result=await send(view,snapshot,ct);}
        catch(BusinessException e){result=JobResult.Of("failed",e.Message);}
        catch(Exception)when(!ct.IsCancellationRequested){result=JobResult.Of("uncertain","Sin respuesta del proveedor; se consulta antes de repetir");}
        return await Record(kind,rid,attempt,actor,result,ct);
    }
    readonly ConcurrentDictionary<string,DateTimeOffset> polled=new();
    // Records waiting on the provider (a customer at the terminal, an open payment link) are asked about every `every`;
    // only an answer that changes something is committed, so waiting adds nothing to the store.
    public async Task<bool> PollNext(string kind,string actor,Func<JsonObject,bool> waiting,Func<BranchState,JsonObject,CancellationToken,Task<JobResult?>> consult,TimeSpan every,CancellationToken ct=default)
    {
        var s=await View(ct);var now=DateTimeOffset.UtcNow;
        var row=s.List(kind).Where(x=>waiting(x)&&!Leased(x,now)&&(!polled.TryGetValue(kind+":"+x.S("id"),out var last)||now-last>=every)).OrderBy(x=>x.S("at"),StringComparer.Ordinal).FirstOrDefault();if(row is null)return false;
        var id=row.S("id");polled[kind+":"+id]=now;var result=await consult(s,(JsonObject)row.DeepClone(),ct);
        if(result is null||result.Status==row.S("status")&&result.Fields.All(f=>J.Canonical(f.Value)==J.Canonical(row[f.Key])))return false;
        return await Record(kind,id,row.S("attemptId"),actor,result,ct);
    }
    async Task<bool> Record(string kind,string id,string attempt,string actor,JobResult result,CancellationToken ct)
    {
        for(int retry=0;retry<8;retry++){
            var s=await Load(ct);var row=s.Get(kind,id);if(row.S("attemptId")!=attempt)return false;row.Remove("leaseUntil");
            Domain.IntegrationResult(s,kind,id,result.Status,result.Fields,result.Summary,result.Reference);
            try{await Commit(s,J.Id(),actor,J.Hash(id+attempt+result.Status),J.O("id",id,"status",s.Get(kind,id).S("status")),kind+".result",ct);polled.TryRemove(kind+":"+id,out _);return true;}
            catch(StoreException e)when(e.Code=="COS-DATA-CONFLICT"&&retry<7){}
        }
        return false;
    }
}
// The live providers with valid credentials, each with its background service. Returns the terminals the domain may charge on.
public static partial class LiveServices
{
    public static JsonArray Register(IServiceCollection services,IntegrationConfig config)
    {
        var terminals=new JsonArray();
        Messaging(services,config);Maps(services,config);CallerId(services,config);Terminals(services,config,terminals);OnlinePay(services,config);Pms(services,config);Aggregators(services,config);
        return terminals;
    }
    static void Worker(IServiceCollection services,string key,Func<BranchService,Func<CancellationToken,Task<bool>>> step,int idleMs=2000)=>services.AddSingleton<IHostedService>(sp=>{var branch=sp.GetRequiredService<BranchService>();return new IntegrationWorker(key,branch,step(branch),sp.GetRequiredService<ILogger<IntegrationWorker>>(),idleMs);});
    // One shared client per provider host: TLS validated by the OS, no redirects, a bounded wait.
    static HttpClient Http(string baseUrl,int seconds=20)=>new(new SocketsHttpHandler{AllowAutoRedirect=false,PooledConnectionLifetime=TimeSpan.FromMinutes(10)}){BaseAddress=new Uri(baseUrl),Timeout=TimeSpan.FromSeconds(seconds)};
}
// Runs one provider's outside work as it appears. A step returns true when it did something, so the next follows at once;
// a provider error shows on its card and the step is tried again later.
public sealed class IntegrationWorker(string key,BranchService branch,Func<CancellationToken,Task<bool>> step,ILogger<IntegrationWorker> log,int idleMs=2000):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();var reported="";
        while(!ct.IsCancellationRequested){
            var delay=idleMs;
            try{if(await step(ct))delay=0;branch.NoteIntegration(key,"lista");reported="";}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(BusinessException e){delay=10000;branch.NoteIntegration(key,"error: "+e.Message);if(e.Message!=reported)log.LogWarning("{Provider} en espera: {Reason}",key,e.Message);reported=e.Message;}
            catch(Exception e){delay=10000;branch.NoteIntegration(key,"sin conexión");if(reported!="network")log.LogWarning("{Provider} sin conexión: {Kind}",key,e.GetType().Name);reported="network";}
            if(delay>0)try{await Task.Delay(delay,ct);}catch(OperationCanceledException){break;}
        }
    }
}
