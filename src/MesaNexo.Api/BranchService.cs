using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
// The committed state lives in memory and a single writer commits to the store at a time. The store stays the
// durable truth: every request checks the meta version, and any doubt (store errors, another process writing)
// rebuilds the state from the store before it is used again.
public sealed partial class BranchService(IBranchStore store,string siteId,IPasswordHasher passwords)
{
    static readonly Dictionary<string,int> Bounded=new(){["audit"]=500};
    readonly SemaphoreSlim writer=new(1,1);readonly AsyncLocal<bool> writing=new();CommittedState? committed;
    Tuple<DateTimeOffset,JsonNode>? health;
    public string CloudState{get;internal set;}="unconfigured";
    public event Action<long>? RevisionChanged;
    public Restaurant Domain {get;}=new(passwords);
    public string SiteId=>siteId;
    public string CentralState {get;private set;}="unverified";public void NoteCentral(string state)=>CentralState=state;
    public async Task<bool> Ready(CancellationToken ct=default)
    {
        var h=await store.CallAsync(J.O("op","health"),ct);if(h?.B("ready")!=true||h.S("site_id")!=siteId)return false;
        var c=await store.CallAsync(J.O("op","capabilities_v1"),ct);if(!c!.A("extensions").Strings().Contains("transaction_v1"))return false;
        var m=await store.CallAsync(J.O("op","get","record_id","mn_meta_site"),ct);return m?.Obj("data").S("siteId")==siteId && m.Obj("data").S("status")=="active";
    }
    // A consistent scan of every record; with `bounded`, kinds in Bounded keep only their newest records.
    async Task<CommittedState> Read(CancellationToken ct,bool bounded=true)
    {
        for(var attempt=0;attempt<8;attempt++){
            var rows=new List<StoredRecord>();string? after=null;long? revision=null;bool changed=false;
            do{
                var page=(await store.CallAsync(J.O("op","scan_v1","protocol",1,"prefix","mn_","after",after??""),ct))!;
                revision??=page.L("revision");if(revision!=page.L("revision")){changed=true;break;}
                foreach(var row in page.A("records").Objects())rows.Add(new(row.S("record_id"),row.L("version"),(JsonObject)row.Obj("data").DeepClone()));
                after=page.S("after");
            }while(!string.IsNullOrEmpty(after));
            var current=await store.CallAsync(J.O("op","get","record_id","mn_meta_site"),ct);
            if(changed||(current?.L("version")??0)!=revision)continue;
            if(bounded)foreach(var (kind,limit) in Bounded){var all=rows.Where(x=>x.Data.S("kind")==kind).ToArray();var keep=all.OrderBy(x=>x.Data.L("sequence")).TakeLast(limit).ToArray();rows=[..rows.Except(all),..keep];}
            var state=new CommittedState(rows,revision??0);
            if(state.Count>0)Need(new BranchState(state,true).Meta.S("siteId")==siteId,"Identidad de sucursal incorrecta",503);return state;
        }throw new BusinessException(409,"El estado cambió; vuelve a consultar");
    }
    async Task<CommittedState> Current(CancellationToken ct)
    {
        var meta=await store.CallAsync(J.O("op","get","record_id","mn_meta_site"),ct);var version=meta?.L("version")??0;
        var c=Volatile.Read(ref committed);if(c is not null&&c.Version>=version)return c;
        // The store can be one commit ahead while this service's writer is still publishing it: wait for the
        // writer instead of rebuilding. Inside the writer nothing is in flight, so a newer store is another process.
        if(c is not null&&!writing.Value){await writer.WaitAsync(ct);writer.Release();c=Volatile.Read(ref committed);if(c is not null&&c.Version>=version)return c;}
        // First use, or another process committed: rebuild, never replacing a newer state published meanwhile.
        var loaded=await Read(ct);
        while(true){var seen=Volatile.Read(ref committed);if(seen is not null&&seen.Version>=loaded.Version)return seen;if(Interlocked.CompareExchange(ref committed,loaded,seen)==seen)return loaded;}
    }
    void Invalidate()=>Volatile.Write(ref committed,null);
    // A writable state: it clones only the records it reaches. Commit rejects it if anything committed since.
    public async Task<BranchState> Load(CancellationToken ct=default)=>new(await Current(ct));
    async Task<BranchState> View(CancellationToken ct)=>new(await Current(ct),true);
    public JsonObject Authenticate(BranchState s,string token,out JsonObject session)
    {
        var hash=J.Hash(token);session=s.Related("session","tokenHash",hash).FirstOrDefault(x=>!x.B("revoked")&&DateTimeOffset.Parse(x.S("expires"))>DateTimeOffset.UtcNow)??throw new BusinessException(401,"Inicia sesión de nuevo");
        var u=s.Get("user",session.S("userId"));Need(u.B("active"),"Usuario desactivado",401);var d=s.Get("device",session.S("deviceId"));Need(d.B("authorized"),"Dispositivo revocado",401);Domain.Usage(s,u);return u;
    }
    public async Task<JsonObject> Initialize(JsonObject config,CancellationToken ct=default)
    {
        var state=await Load(ct);Need(state.Count==0,"La sucursal ya está inicializada; no se sobrescribe",409);
        Need(config.S("siteId")==siteId&&System.Text.RegularExpressions.Regex.IsMatch(siteId,"^[a-z0-9-]{1,32}$"),"Identidad no coincide con el runtime");Need(!config.S("managerPassword").StartsWith("REEMPLAZAR",StringComparison.OrdinalIgnoreCase)&&config.S("enrollmentCode").Length>=20&&!config.S("enrollmentCode").StartsWith("REEMPLAZAR",StringComparison.OrdinalIgnoreCase),"Reemplaza los marcadores de ejemplo por credenciales propias");
        var mode=Choice(config,"mode","renta","licencia","codigo");var modules=config.A("modules").Strings();Need(modules.All(Restaurant.Modules.Contains),"Módulos desconocidos");
        var activation=DateTimeOffset.UtcNow;
        state.Add("meta",J.O("id","site","siteId",siteId,"name",Text(config,"name",150),"status","active","mode",mode,"usageUntil",mode=="renta"?config.S("usageUntil"):null,"supportUntil",mode=="licencia"?activation.AddYears(1).ToString("O"):config.S("supportUntil"),"hostingUntil",mode=="licencia"?activation.AddMonths(2).ToString("O"):config.S("hostingUntil"),"modules",modules,"maxUsers",config.L("maxUsers",15),"maxTables",config.L("maxTables",30),"color","#F7BB32","stations",new[]{"Cocina","Barra","Parrilla","Postres"},"discountLimit",30,"delayMinutes",15,"enrollmentHash",J.Hash(Text(config,"enrollmentCode",200)),"updates",mode!="codigo","updatesUntil",mode=="licencia"?activation.AddYears(1).ToString("O"):null,"revision",0,"schemaVersion",2));
        state.Add("user",J.O("id",J.Id(),"siteId",siteId,"name",Text(config,"managerName",100),"username",Text(config,"managerUsername",100),"role","manager","active",true,"permissions",J.O("payments",true,"shiftReports",true),"password",passwords.Hash(config.S("managerPassword"))));
        return await Commit(state,J.Id(),"bootstrap",J.Hash(J.Canonical(config)),J.O("initialized",true,"siteId",siteId),"branch.initialize",ct);
    }
    async Task<JsonObject> Commit(BranchState s,string eventId,string actor,string fingerprint,JsonObject result,string type,CancellationToken ct)
    {
        var owned=!writing.Value;if(owned)await writer.WaitAsync(ct);long revision;
        try{
            // Same rule as the store's per-record versions, checked first in memory: a state built before another commit is stale.
            if(Volatile.Read(ref committed) is not {} current||current.Version!=s.Committed.Version)throw new StoreException("COS-DATA-CONFLICT");
            AppendAudit(s,eventId,actor,fingerprint,type);
            s.Meta["revision"]=revision=s.Meta.L("revision")+1;
            var receipt=J.O("actor",actor,"fingerprint",fingerprint,"operation",type,"result",result,"at",J.Now());
            var changes=s.Changes().ToArray();JsonNode response;
            try{response=(await store.CallAsync(J.O("op","transaction_v1","protocol",1,"event_id",eventId,"receipt",receipt,"changes",new JsonArray(changes.Select(x=>(JsonNode)x.DeepClone()).ToArray())),ct))!;}
            // Rejections the store makes before writing leave it as it was; anything else may have committed, so reload.
            catch(StoreException e)when(e.Code is "COS-DATA-INVALID" or "COS-DATA-FULL" or "COS-DATA-IDEMPOTENCY"){throw;}
            catch{Invalidate();throw;}
            if(response.B("duplicate")){Invalidate();return (JsonObject)response.Obj("receipt").Obj("result").DeepClone();}
            // Re-parsed exactly as a store read returns it, so values set as int, decimal, etc. read back the same way.
            var stored=changes.Select(c=>new StoredRecord(c.S("record_id"),c.L("expected_version")+1,JsonNode.Parse(c.Obj("data").ToJsonString())!.AsObject())).ToArray();
            Volatile.Write(ref committed,current.With(stored,stored.Single(x=>x.RecordId=="mn_meta_site").Version,Bounded));
            result=(JsonObject)response.Obj("receipt").Obj("result").DeepClone();
        }finally{if(owned)writer.Release();}
        try{RevisionChanged?.Invoke(revision);}catch{}
        return result;
    }
    // Serializes every request that mutates the branch; Commit inside it does not wait again.
    async Task<T> Exclusive<T>(Func<Task<T>> work,CancellationToken ct)
    {
        await writer.WaitAsync(ct);writing.Value=true;
        try{return await work();}finally{writing.Value=false;writer.Release();}
    }
    static string Operation(string id){Need(Guid.TryParse(id,out var guid),"operationId debe ser UUID estable");return guid.ToString("N");}
    public async Task<JsonObject> Login(JsonObject p,CancellationToken ct=default)
    {
        var token=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));var deviceId=Operation(p.S("deviceId"));Need(p.S("deviceSecret").Length is >=32 and <=200,"Credencial de dispositivo requerida");
        // Argon2 runs before taking the writer so a slow verification never holds back other terminals.
        var candidate=(await View(ct)).List("user").FirstOrDefault(x=>x.S("username")==p.S("username")&&x.B("active"));Need(candidate is not null&&passwords.Verify(candidate.S("password"),p.S("password")),"Usuario o contraseña incorrectos",401);
        return await Exclusive(async()=>{
            for(var attempt=0;attempt<3;attempt++){
                var s=await Load(ct);var user=s.Get("user",candidate!.S("id"));Need(user.B("active")&&user.S("username")==candidate.S("username")&&user.S("password")==candidate.S("password"),"Usuario o contraseña incorrectos",401);Domain.Usage(s,user);
                var device=s.List("device").FirstOrDefault(x=>x.S("id")==deviceId);
                if(device is null){Need(p.S("enrollmentCode")!=""&&J.Hash(p.S("enrollmentCode"))==s.Meta.S("enrollmentHash"),"El dispositivo requiere un código de alta del administrador",403);device=s.Add("device",J.O("id",deviceId,"siteId",siteId,"name",Text(p,"deviceName",100),"secretHash",J.Hash(p.S("deviceSecret")),"authorized",true,"at",J.Now()));s.Meta["enrollmentHash"]="";}
                Need(device.S("secretHash")==J.Hash(p.S("deviceSecret"))&&device.B("authorized"),"Dispositivo no autorizado",403);
                var session=s.Add("session",J.O("siteId",siteId,"userId",user.S("id"),"deviceId",deviceId,"tokenHash",J.Hash(token),"at",J.Now(),"expires",DateTimeOffset.UtcNow.AddHours(12).ToString("O"),"revoked",false));
                // A conflict here only follows another process writing the store; the state was reloaded.
                try{await Commit(s,J.Id(),user.S("id"),J.Hash("login"),J.O("sessionId",session.S("id")),"session.login",ct);return J.O("token",token,"sessionId",session.S("id"),"user",Domain.SafeUser(user),"siteId",siteId);}catch(StoreException e)when(e.Code=="COS-DATA-CONFLICT"&&attempt<2){}
            }throw new BusinessException(409,"Conflicto al iniciar sesión");
        },ct);
    }
    // Test safety net (MESANEXO_VERIFY_VIEWS=1): a read must leave the shared committed state exactly as it found it.
    static readonly bool VerifyViews=Environment.GetEnvironmentVariable("MESANEXO_VERIFY_VIEWS")=="1";
    // Diagnostics (MESANEXO_PROFILE=1): per action, domain time, total time to commit and records it reached.
    static readonly bool Profile=Environment.GetEnvironmentVariable("MESANEXO_PROFILE")=="1";
    static string Fingerprint(CommittedState c)=>J.Hash(string.Concat(c.Records.Values.OrderBy(x=>x.RecordId,StringComparer.Ordinal).Select(x=>x.RecordId+(x.Data.Parent is null?"":"!attached")+J.Canonical(x.Data))));
    public async Task<JsonObject> State(string token,CancellationToken ct=default)
    {
        var s=await View(ct);var before=VerifyViews?Fingerprint(s.Committed):null;var u=Authenticate(s,token,out var sess);var result=Domain.Snapshot(s,u,sess.S("id"));
        if(before is not null&&Fingerprint(s.Committed)!=before)throw new InvalidOperationException("COS-APP-VIEW-MUTATED: una lectura modificó el estado compartido");
        // The outbox count is informative; refreshing it every two seconds keeps polling off the store.
        var cached=Volatile.Read(ref health);if(cached is null||DateTimeOffset.UtcNow-cached.Item1>TimeSpan.FromSeconds(2))Volatile.Write(ref health,cached=Tuple.Create(DateTimeOffset.UtcNow,(await store.CallAsync(J.O("op","health"),ct))!));
        result["connection"]=J.O("local","available","outboxPending",cached.Item2.L("pending"),"central",CentralState,"internet","unverified","cloudOrders",CloudState);return result;
    }
    public async Task<JsonObject> Action(string token,JsonObject request,CancellationToken ct=default)
    {
        var eventId=Operation(request.S("operationId"));var type=Text(request,"type",100);var p=request.Obj("payload");
        Need(!p.Any(x=>new[]{"cvv","pin","pan","cardNumber"}.Contains(x.Key,StringComparer.OrdinalIgnoreCase)),"No se admiten datos bancarios sensibles");
        return await Exclusive(async()=>{
            for(var attempt=0;attempt<3;attempt++){
                var s=await Load(ct);var u=Authenticate(s,token,out var session);var printable=(JsonObject)p.DeepClone();printable.Remove("passcode");var fingerprint=J.Hash(J.Canonical(J.O("actor",u.S("id"),"type",type,"payload",printable,"siteId",siteId)));
                var prior=await store.CallAsync(J.O("op","get","record_id","tx_"+eventId),ct);
                if(prior is not null){var receipt=prior.Obj("data");Need(receipt.S("actor")==u.S("id")&&receipt.S("fingerprint")==fingerprint,"ID de operación reutilizado con otro contenido",409);return J.O("operationId",eventId,"local","committed","duplicate",true,"result",receipt.Obj("result"));}
                JsonObject result;var watch=Profile?System.Diagnostics.Stopwatch.StartNew():null;
                if(type=="session.logout"){session["revoked"]=true;result=J.O("ok",true);}
                else result=Domain.Act(s,u,type,p);
                var acted=watch?.Elapsed.TotalMilliseconds;var touched=s.Touched;
                // A conflict here only follows another process writing the store; the state was reloaded.
                try{var r=await Commit(s,eventId,u.S("id"),fingerprint,result,type,ct);if(watch is not null)Console.Error.WriteLine($"PROFILE {type} act={acted:0.0} total={watch.Elapsed.TotalMilliseconds:0.0} touched={touched} records={s.Committed.Count}");return J.O("operationId",eventId,"local","committed","duplicate",false,"result",r);}catch(StoreException e)when(e.Code=="COS-DATA-CONFLICT"&&attempt<2){}
            }throw new BusinessException(409,"Conflicto concurrente; conserva el mismo identificador y reintenta");
        },ct);
    }
    public async Task<JsonObject> OperationStatus(string token,string id,CancellationToken ct=default)
    {
        var s=await View(ct);var u=Authenticate(s,token,out _);var eventId=Operation(id);var row=await store.CallAsync(J.O("op","get","record_id","tx_"+eventId),ct);if(row is null)return J.O("operationId",eventId,"local","unknown","remote","unknown");var receipt=row.Obj("data");Need(receipt.S("actor")==u.S("id"),"Operación de otro usuario",403);var status=(JsonObject)(await store.CallAsync(J.O("op","event_status_v1","event_id",eventId),ct))!;status["result"]=receipt.Obj("result").DeepClone();status["operationId"]=eventId;return status;
    }
                public async Task<JsonObject> Preview(string token,string visitId,CancellationToken ct=default)
    {
        var s=await View(ct);var u=Authenticate(s,token,out _);Role(u,"mesero","manager");var v=s.Get("visit",visitId);Need(u.S("role")=="manager"||v.S("waiterId")==u.S("id"),"Cuenta de otro mesero",403);Need(!DateTimeOffset.TryParse(v.S("trackingExpires"),out var expiry)||expiry>DateTimeOffset.UtcNow,"Seguimiento vencido",410);return J.O("internalPreview",true,"key",v.S("trackingKey"),"link","mesanexo://seguimiento/"+v.S("trackingKey"),"expires",v["trackingExpires"],"units",s.Related("unit","visitId",visitId).Select(x=>J.O("name",x.S("name"),"status",x.S("status"),"notes",x.S("notes"))).ToArray());
    }
    public async Task<JsonObject> SupportPreview(string token,CancellationToken ct=default)
    {
        var s=await Load(ct);var grant=s.Related("grant","tokenHash",J.Hash(token)).FirstOrDefault(x=>!x.B("revoked")&&DateTimeOffset.Parse(x.S("expires"))>DateTimeOffset.UtcNow);Need(grant is not null,"Consentimiento vencido, revocado o inválido",403);Need(DateTimeOffset.TryParse(s.Meta.S("supportUntil"),out var until)&&until>DateTimeOffset.UtcNow,"Cobertura de soporte vencida",403);var manager=s.Get("user",grant!.S("authorizedBy"));Need(manager.B("active")&&manager.S("role")=="manager","Gerente autorizante inactivo",403);var snapshot=Domain.Snapshot(s,manager,"support-readonly");await Commit(s,J.Id(),"authorized-support",J.Hash(grant.S("id")),J.O("audited",true),"support.preview",ct);return J.O("simulation",true,"scope","read-only-business-preview","data",snapshot);
    }
    public async Task<JsonObject> CanInstall(JsonObject device,CancellationToken ct=default)
    {
        var s=await View(ct);Need(device.S("siteId")==siteId,"Sucursal incorrecta",403);var d=s.Get("device",device.S("deviceId"));Need(d.B("authorized")&&d.S("secretHash")==J.Hash(device.S("deviceSecret")),"Dispositivo no autorizado",403);
        var sessions=s.Related("session","deviceId",d.S("id")).Any(x=>!x.B("revoked")&&DateTimeOffset.Parse(x.S("expires"))>DateTimeOffset.UtcNow);var accounts=s.Related("visit","status","open").Any();var covered=s.Meta.B("updates")&&(!DateTimeOffset.TryParse(s.Meta.S("updatesUntil"),out var until)||until>DateTimeOffset.UtcNow);
        return J.O("allowed",!sessions&&!accounts&&covered,"policy","Cerrar sesión y todas las cuentas; requiere cobertura local de actualizaciones");
    }
    public async Task RotateEnrollment(string code,CancellationToken ct=default){Need(code.Length>=20,"Código mínimo de 20 caracteres");await Exclusive(async()=>{var s=await Load(ct);s.Meta["enrollmentHash"]=J.Hash(code);return await Commit(s,J.Id(),"local-admin",J.Hash("enrollment"),J.O("ok",true),"device.enrollment.rotate",ct);},ct);}
}
