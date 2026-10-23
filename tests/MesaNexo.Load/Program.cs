using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MesaNexo.Api;
using MesaNexo.Client;
using MesaNexo.Core;
using Microsoft.Extensions.DependencyInjection;
namespace MesaNexo.Load;

// Prueba de carga contra el backend real: Kestrel HTTPS, BranchService y el almacén cifrado de laboratorio.
// Cada terminal se comporta como la interfaz: consulta /api/state cada pollMs y envía una mutación a la vez,
// reintentando con el mismo operationId sólo cuando la respuesta es incierta o hay conflicto concurrente.
sealed record Sample(string Kind,double Ms,string Outcome);
sealed record Ticket(string VisitId,int Units,long Paid);
sealed record KitchenJob(string[] UnitIds,TaskCompletionSource Done);
sealed class LoadFailure(int status,string message):Exception(message){public int Status=>status;}
sealed class Metrics
{
    readonly ConcurrentQueue<Sample> pending=new();public readonly List<Sample> All=[];public long Retries,DuplicateReplies;
    public void Add(string kind,double ms,string outcome)=>pending.Enqueue(new(kind,ms,outcome));
    public List<Sample> Drain(){var list=new List<Sample>();while(pending.TryDequeue(out var x))list.Add(x);All.AddRange(list);return list;}
    static long P(double[] sorted,double q)=>sorted.Length==0?0:(long)Math.Round(sorted[Math.Clamp((int)Math.Ceiling(q*sorted.Length)-1,0,sorted.Length-1)]);
    public static JsonObject Summary(IEnumerable<Sample> samples)
    {
        var a=samples.ToArray();var ok=a.Where(x=>x.Outcome=="ok").Select(x=>x.Ms).Order().ToArray();
        return J.O("requests",a.Length,"ok",ok.Length,"errors",a.Length-ok.Length,"p50Ms",P(ok,.5),"p95Ms",P(ok,.95),"p99Ms",P(ok,.99),"maxMs",P(ok,1),"outcomes",a.GroupBy(x=>x.Outcome).ToDictionary(g=>g.Key,g=>g.Count()));
    }
}
sealed class Terminal(string name,HttpClient http,Metrics metrics):IDisposable
{
    public string Token{get;private set;}="";int acting;
    public bool Acting=>Volatile.Read(ref acting)==1;
    async Task<(int Status,JsonObject Body)> Timed(string kind,HttpMethod method,string path,JsonObject? body,CancellationToken ct)
    {
        var watch=Stopwatch.StartNew();
        try{
            using var request=new HttpRequestMessage(method,path);if(Token!="")request.Headers.Authorization=new("Bearer",Token);if(body is not null)request.Content=new StringContent(body.ToJsonString(),Encoding.UTF8,"application/json");
            using var response=await http.SendAsync(request,ct);var raw=await response.Content.ReadAsStringAsync(ct);var status=(int)response.StatusCode;
            metrics.Add(kind,watch.Elapsed.TotalMilliseconds,status==200?"ok":status.ToString(CultureInfo.InvariantCulture));
            return (status,(raw.Length>0?JsonNode.Parse(raw) as JsonObject:null)??new JsonObject());
        }catch(Exception e)when(!ct.IsCancellationRequested&&e is TaskCanceledException or HttpRequestException or IOException){metrics.Add(kind,watch.Elapsed.TotalMilliseconds,"timeout");return (0,new JsonObject());}
    }
    public async Task Login(string site,string username,string password,string enrollment)
    {
        var p=J.O("deviceId",Guid.NewGuid().ToString(),"deviceSecret",Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),"deviceName","Carga "+name,"enrollmentCode",enrollment,"siteId",site,"username",username,"password",password);
        // En laboratorio todas las terminales salen de 127.0.0.1: se respeta el límite de 8 logins/min por IP esperando la ventana.
        for(var attempt=0;;attempt++){
            var (status,body)=await Timed("login",HttpMethod.Post,"api/login",p,CancellationToken.None);
            if(status==200){Token=body.S("token");return;}
            // El limitador de ASP.NET rechaza con 503 sin cuerpo (no 429) mientras no se configure RejectionStatusCode.
            var limited=status==429||status==503&&body.Count==0;
            if(!limited||attempt>=12)throw new LoadFailure(status,"Login de "+name+" ("+status+"): "+body.S("error"));
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
    }
    public async Task State(CancellationToken ct)=>await Timed("state",HttpMethod.Get,"api/state",null,ct);
    public async Task<JsonObject> Act(string type,JsonObject payload,CancellationToken ct)
    {
        var request=J.O("operationId",Guid.NewGuid().ToString(),"type",type,"payload",payload);Volatile.Write(ref acting,1);
        try{
            for(var attempt=0;;attempt++){
                var (status,body)=await Timed(type,HttpMethod.Post,"api/action",request,ct);
                if(status==200){if(body.B("duplicate"))Interlocked.Increment(ref metrics.DuplicateReplies);return body.Obj("result");}
                var retryable=status is 0 or 503||(status==409&&!body.S("error").Contains("reutilizado"));
                if(!retryable||attempt>=5)throw new LoadFailure(status,type+": "+(status==0?"sin respuesta":body.S("error")));
                Interlocked.Increment(ref metrics.Retries);await Task.Delay(250*(attempt+1),ct);
            }
        }finally{Volatile.Write(ref acting,0);}
    }
    public void Dispose()=>http.Dispose();
}
static class Program
{
    const string Site="load-site";
    static int FreePort(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var p=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return p;}
    static async Task<int> Main(string[] args)
    {
        string Arg(string key,string fallback)=>args.SkipWhile(x=>x!="--"+key).Skip(1).FirstOrDefault()??fallback;
        var root=Path.GetFullPath(Arg("root",Directory.GetCurrentDirectory()));var python=Arg("python",@"C:\Users\kikin\Desktop\CulinaryOS\tools\python\python.exe");
        var waiters=int.Parse(Arg("waiters","6"),CultureInfo.InvariantCulture);var tableCount=int.Parse(Arg("tables","24"),CultureInfo.InvariantCulture);var minutes=double.Parse(Arg("minutes","15"),CultureInfo.InvariantCulture);
        var pollMs=int.Parse(Arg("poll-ms","2000"),CultureInfo.InvariantCulture);var thinkMs=int.Parse(Arg("think-ms","600"),CultureInfo.InvariantCulture);var sampleSeconds=int.Parse(Arg("sample-seconds","30"),CultureInfo.InvariantCulture);
        var output=Path.GetFullPath(Arg("output",Path.Combine(root,"evidence","load-baseline.json")));
        // Límites del contrato por defecto: 15 usuarios (gerente + meseros + 2 cocina) y 30 mesas.
        if(waiters is <1 or >12||tableCount<waiters||tableCount>30||pollMs<100||thinkMs<2){Console.Error.WriteLine("Parámetros fuera de rango: 1-12 meseros, mesas entre meseros y 30.");return 2;}
        var folder=Path.Combine(root,".lab","load-"+DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture));Directory.CreateDirectory(folder);
        using var cert=LabCertificate.Create(folder);var port=FreePort();
        using var runtime=new RuntimeStore(python,Path.Combine(root,"runtime","lab_host.py"),folder,Site);
        await using var app=ApiHost.Build(new(Site,port,"127.0.0.1",ReadySocket:null),runtime,cert);await app.StartAsync();var service=app.Services.GetRequiredService<BranchService>();
        var profile=new ConnectionProfile($"https://localhost:{port}",Site,Path.Combine(folder,"lab-ca.crt"),cert.GetCertHashString(HashAlgorithmName.SHA256));
        var metrics=new Metrics();var password="Carga-"+J.Id()+"!";var none=CancellationToken.None;
        await service.Initialize(J.O("siteId",Site,"name","Prueba de carga","mode","licencia","managerName","Gerencia","managerUsername","gerencia","managerPassword",password,"enrollmentCode",J.Id(),"modules",Restaurant.Modules));
        async Task<Terminal> Open(string name,string username){var code=J.Id();await service.RotateEnrollment(code);var t=new Terminal(name,TrustedConnection.Create(profile),metrics);await t.Login(Site,username,password,code);return t;}
        Console.WriteLine($"Preparando sucursal: {waiters} meseros, 2 cocina, 1 gerencia, {tableCount} mesas, sondeo cada {pollMs} ms.");
        var manager=await Open("Gerencia","gerencia");
        for(var i=1;i<=waiters;i++)await manager.Act("user.save",J.O("name","Mesero "+i,"username","mesero"+i,"role","mesero","password",password,"active",true),none);
        for(var i=1;i<=2;i++)await manager.Act("user.save",J.O("name","Cocina "+i,"username","cocina"+i,"role","cocina","password",password,"active",true),none);
        await manager.Act("catalog.category",J.O("name","Principales"),none);
        var tacos=(await manager.Act("catalog.product",J.O("name","Tacos al pastor","price",14500,"category","Principales","station","Cocina"),none)).S("id");
        var agua=(await manager.Act("catalog.product",J.O("name","Agua de horchata","price",3500,"category","Principales","station","Barra"),none)).S("id");
        var tables=new List<string>();for(var i=1;i<=tableCount;i++)tables.Add((await manager.Act("table.save",J.O("name",$"Mesa {i:00}","capacity",4,"zone","Salón"),none)).S("id"));
        await manager.Act("cash.open",J.O("initial",50000),none);
        var waiterTerminals=new List<Terminal>();for(var i=1;i<=waiters;i++)waiterTerminals.Add(await Open("Mesero "+i,"mesero"+i));
        var kitchenTerminals=new List<Terminal>();for(var i=1;i<=2;i++)kitchenTerminals.Add(await Open("Cocina "+i,"cocina"+i));
        metrics.Drain();metrics.All.Clear();
        const long ticketTotal=2*14500+3500;
        using var cts=new CancellationTokenSource();var ct=cts.Token;
        var kitchenQueue=Channel.CreateUnbounded<KitchenJob>();var tickets=new ConcurrentQueue<Ticket>();var aborted=new ConcurrentQueue<string>();var failures=new ConcurrentDictionary<string,int>();
        void Fail(Exception e)=>failures.AddOrUpdate(e.Message.Length>160?e.Message[..160]:e.Message,1,(_,n)=>n+1);
        Task Think()=>Task.Delay(Random.Shared.Next(thinkMs/2,thinkMs*3/2+1),ct);
        async Task Waiter(Terminal t,string[] own)
        {
            var dead=new HashSet<string>();
            while(!ct.IsCancellationRequested&&dead.Count<own.Length)foreach(var table in own.Where(x=>!dead.Contains(x))){
                var visit="";var closed=false;
                try{
                    visit=(await t.Act("visit.open",J.O("tableId",table,"people",2),ct)).S("id");await Think();
                    var units=(await t.Act("command.send",J.O("visitId",visit,"items",new[]{J.O("productId",tacos,"quantity",2),J.O("productId",agua,"quantity",1)}),ct)).A("unitIds").Strings();
                    var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);await kitchenQueue.Writer.WriteAsync(new(units,done),ct);await done.Task.WaitAsync(ct);
                    await t.Act("visit.deliverAll",J.O("id",visit),ct);await Think();
                    await t.Act("payment.add",J.O("visitId",visit,"amount",ticketTotal,"method",Random.Shared.Next(2)==0?"cash":"card"),ct);
                    await t.Act("visit.close",J.O("id",visit),ct);closed=true;tickets.Enqueue(new(visit,units.Length,ticketTotal));
                    await t.Act("table.clean",J.O("id",table),ct);await Think();
                }catch(OperationCanceledException)when(ct.IsCancellationRequested){if(visit!=""&&!closed)aborted.Enqueue(visit);return;}
                catch(Exception e){Fail(e);dead.Add(table);if(visit!=""&&!closed)aborted.Enqueue(visit);}
            }
        }
        async Task Kitchen(Terminal t)
        {
            try{
                await foreach(var job in kitchenQueue.Reader.ReadAllAsync(ct)){
                    try{foreach(var unit in job.UnitIds){await t.Act("unit.state",J.O("id",unit,"status","preparing"),ct);await t.Act("unit.state",J.O("id",unit,"status","ready"),ct);}job.Done.TrySetResult();}
                    catch(Exception e)when(!ct.IsCancellationRequested){Fail(e);job.Done.TrySetException(e);}
                }
            }catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        }
        async Task Poll(Terminal t)
        {
            try{while(true){await Task.Delay(pollMs,ct);if(!t.Acting)await t.State(ct);}}catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        }
        var tasks=new List<Task>();
        for(var i=0;i<waiters;i++){var own=tables.Where((_,n)=>n%waiters==i).ToArray();tasks.Add(Waiter(waiterTerminals[i],own));}
        tasks.AddRange(kitchenTerminals.Select(Kitchen));tasks.AddRange(waiterTerminals.Concat(kitchenTerminals).Append(manager).Select(Poll));
        Console.WriteLine($"Carga iniciada por {minutes} min; muestra cada {sampleSeconds} s. Se detiene antes si el servicio colapsa.");
        var windows=new JsonArray();var started=Stopwatch.StartNew();var bad=0;var previous=0;var verdict="completó la duración prevista";
        while(started.Elapsed.TotalMinutes<minutes){
            await Task.Delay(TimeSpan.FromSeconds(sampleSeconds));
            var samples=metrics.Drain();var watch=Stopwatch.StartNew();int records;try{records=(await service.Load()).Records.Count;}catch{records=-1;}var loadMs=(long)watch.Elapsed.TotalMilliseconds;
            var state=Metrics.Summary(samples.Where(x=>x.Kind=="state"));var actions=Metrics.Summary(samples.Where(x=>x.Kind is not "state" and not "login"));
            var count=tickets.Count;var delta=count-previous;previous=count;var requests=samples.Count;var errors=samples.Count(x=>x.Outcome!="ok");
            windows.Add(J.O("seconds",(long)started.Elapsed.TotalSeconds,"records",records,"internalLoadMs",loadMs,"ticketsClosed",count,"ticketsInWindow",delta,"state",state,"actions",actions,"retries",Interlocked.Read(ref metrics.Retries)));
            Console.WriteLine($"[{started.Elapsed:mm\\:ss}] registros {records,6} · load interno {loadMs/1000.0,5:0.00} s · cuentas cerradas {count,4} (+{delta,3}) · state p50/p95 {state.L("p50Ms"),5}/{state.L("p95Ms"),5} ms · acciones p50/p95 {actions.L("p50Ms"),5}/{actions.L("p95Ms"),5} ms · errores {errors}/{requests}");
            bad=requests>0&&errors*2>=requests||delta==0?bad+1:0;
            if(bad>=2){verdict="colapso: dos muestras seguidas sin cuentas cerradas o con la mitad de las solicitudes fallidas";break;}
        }
        Console.WriteLine("Deteniendo la carga: "+verdict);cts.Cancel();await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60)).ContinueWith(_=>{});metrics.Drain();
        Console.WriteLine("Verificando integridad del almacén (puede tardar con muchos registros)...");
        JsonObject verification;var integrity=false;
        try{
            // En proceso: el mismo dominio y almacén, sin el límite de 15 s del cliente HTTP.
            var snapshot=await service.State(manager.Token);
            var units=snapshot.A("units").Objects().GroupBy(x=>x.S("visitId")).ToDictionary(g=>g.Key,g=>g.Count());
            var paid=snapshot.A("payments").Objects().GroupBy(x=>x.S("visitId")).ToDictionary(g=>g.Key,g=>g.Sum(x=>x.L("amount")));
            var visits=snapshot.A("visits").Objects().ToDictionary(x=>x.S("id"),x=>x.S("status"));
            var closed=tickets.ToArray();var audit=await service.VerifyAudit(manager.Token);
            var notClosed=closed.Count(x=>visits.GetValueOrDefault(x.VisitId)!="closed");var unitMismatch=closed.Count(x=>units.GetValueOrDefault(x.VisitId)!=x.Units);var paymentMismatch=closed.Count(x=>paid.GetValueOrDefault(x.VisitId)!=x.Paid);
            var duplicated=units.Count(x=>x.Value>3);
            integrity=notClosed==0&&unitMismatch==0&&paymentMismatch==0&&duplicated==0&&audit.B("valid");
            verification=J.O("ticketsVerified",closed.Length,"notClosed",notClosed,"unitMismatch",unitMismatch,"paymentMismatch",paymentMismatch,"visitsWithDuplicatedUnits",duplicated,"ledgerPaid",closed.Sum(x=>x.Paid),"storePaidForLedger",closed.Sum(x=>paid.GetValueOrDefault(x.VisitId)),"abortedTickets",aborted.Count,"auditValid",audit.B("valid"),"auditRecords",audit.L("chainedRecords"),"integrity",integrity);
        }catch(Exception e){
            var sessions=(await service.Load()).List("session").ToArray();var hash=J.Hash(manager.Token);var mine=sessions.Where(x=>x.S("tokenHash")==hash).ToArray();
            verification=J.O("error",e.GetType().Name+": "+e.Message,"integrity",false,"sessions",sessions.Length,"managerSessions",mine.Length,"managerSessionState",new JsonArray(mine.Select(x=>(JsonNode)J.O("revoked",x.B("revoked"),"expires",x.S("expires"))).ToArray()));
        }
        var all=metrics.All;
        var report=J.O("generatedAt",J.Now(),
            "scenario",J.O("waiters",waiters,"kitchen",2,"manager",1,"tables",tableCount,"pollMs",pollMs,"thinkMs",thinkMs,"plannedMinutes",minutes,"ticket","2 tacos + 1 agua, cobro en efectivo o tarjeta"),
            "store","Laboratorio: lab_host.py sobre el mismo almacén cifrado de CulinaryOS, por tubería en vez de socket Unix (cota optimista: en producción cada página abre un socket nuevo).",
            "verdict",verdict,"elapsedSeconds",(long)started.Elapsed.TotalSeconds,"windows",windows,
            "totals",J.O("state",Metrics.Summary(all.Where(x=>x.Kind=="state")),"actions",Metrics.Summary(all.Where(x=>x.Kind is not "state" and not "login")),"byType",new JsonObject(all.GroupBy(x=>x.Kind).OrderBy(g=>g.Key).Select(g=>KeyValuePair.Create(g.Key,(JsonNode?)Metrics.Summary(g))))),
            "retries",Interlocked.Read(ref metrics.Retries),"duplicateReplies",Interlocked.Read(ref metrics.DuplicateReplies),"failures",failures.ToDictionary(x=>x.Key,x=>x.Value),"verification",verification);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);await File.WriteAllTextAsync(output,report.ToJsonString(new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
        Console.WriteLine("Integridad: "+(integrity?"correcta":"FALLÓ")+" · "+verification.ToJsonString());Console.WriteLine("Informe: "+output);
        foreach(var t in waiterTerminals.Concat(kitchenTerminals).Append(manager))t.Dispose();
        await app.StopAsync();return integrity?0:1;
    }
}
