using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using MesaNexo.Api;
using MesaNexo.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.RateLimiting;
namespace MesaNexo.Central;
public static class CentralHost
{
    public static WebApplication Build(ProviderService service,string providerId,string packages,X509Certificate2 certificate,int adminPort=9443,int receiverPort=9444,string bind="0.0.0.0")
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{Args=[],ContentRootPath=AppContext.BaseDirectory});builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>{
            k.Limits.MaxRequestBodySize=512*1024*1024;k.Listen(IPAddress.Parse(bind),adminPort,l=>l.UseHttps(certificate));k.Listen(IPAddress.Parse(bind),receiverPort,l=>l.UseHttps(o=>{o.ServerCertificate=certificate;o.ClientCertificateMode=ClientCertificateMode.RequireCertificate;o.ClientCertificateValidation=(cert,_,_)=>service.AllowedCertificate(cert);}));
        });
        builder.Services.AddRateLimiter(o=>o.AddPolicy("login",ctx=>RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=8,Window=TimeSpan.FromMinutes(1),QueueLimit=0})));
        var app=builder.Build();app.Use(async(c,next)=>{c.Response.Headers.CacheControl="no-store";try{await next();}catch(BusinessException e){c.Response.StatusCode=e.Status;await c.Response.WriteAsJsonAsync(J.O("error",e.Message));}catch{c.Response.StatusCode=503;await c.Response.WriteAsJsonAsync(J.O("error","Servicio central no disponible"));}});app.UseRateLimiter();
        static string Token(HttpContext c){var h=c.Request.Headers.Authorization.ToString();Rules.Need(h.StartsWith("Bearer ")&&h.Length<200,"Autenticación requerida",401);return h[7..];}
        static async Task<JsonObject> Body(HttpContext c){Rules.Need(c.Request.ContentLength is null or <=112*1024,"Solicitud demasiado grande",413);return (await JsonNode.ParseAsync(c.Request.Body))!.AsObject();}
        app.MapGet("/api/identity",()=>J.O("name","Culinary Restaurant Proveedor","siteId",providerId,"protocol",1));
        app.MapPost("/api/login",async(HttpContext c)=>{var p=await Body(c);return p.S("tenantId")==""?service.Login(p):service.LoginTenant(p);}).RequireRateLimiting("login");
        app.MapGet("/api/backups/{id}",(string id,HttpContext c)=>Results.File(service.BackupFile(Token(c),id),"application/octet-stream","mesa-nexo-central-"+id+".sqlite"));
        app.MapGet("/api/state",(HttpContext c)=>Token(c).StartsWith("tenant.")?service.StateTenant(Token(c),c.Request.Query["branchId"].ToString()):service.State(Token(c)));
        app.MapPost("/api/action",async(HttpContext c)=>{var p=await Body(c);if(Token(c).StartsWith("tenant.")){Rules.Need(p.S("type")=="session.logout","Dueño sin permiso de operación local",403);service.LogoutTenant(Token(c));return J.O("local","committed","result",J.O("ok",true));}return service.Action(Token(c),p);});
        app.MapGet("/api/operations/{id}",(string id,HttpContext c)=>service.Status(Token(c),id));
        app.MapPost("/v1/events",async(HttpContext c)=>{
            Rules.Need(c.Connection.LocalPort==receiverPort,"Usa el listener mTLS",403);var cert=await c.Connection.GetClientCertificateAsync();Rules.Need(cert is not null,"mTLS requerido",403);using var buffer=new MemoryStream();var chunk=new byte[8192];int n;while((n=await c.Request.Body.ReadAsync(chunk))>0){Rules.Need(buffer.Length+n<=64*1024,"Evento demasiado grande",413);await buffer.WriteAsync(chunk.AsMemory(0,n));}var raw=buffer.ToArray();var node=JsonNode.Parse(raw)!;Rules.Need(c.Request.Headers["Idempotency-Key"].ToString()==node.S("event_id"),"Cabecera de evento no coincide");return service.Receive(raw,c.Request.Headers["X-CulinaryOs-SHA256"].ToString(),cert!);
        });
                app.MapGet("/v1/entitlement",async(HttpContext c)=>{Rules.Need(c.Connection.LocalPort==receiverPort,"mTLS requerido",403);var cert=await c.Connection.GetClientCertificateAsync();Rules.Need(cert is not null,"mTLS requerido",403);return service.Entitlement(cert!);});
        app.MapGet("/v1/cloud/pending",async(HttpContext c)=>{Rules.Need(c.Connection.LocalPort==receiverPort,"Usa listener mTLS",403);var cert=await c.Connection.GetClientCertificateAsync();Rules.Need(cert is not null,"Certificado requerido",403);return service.PendingCloud(cert!);});
        app.MapPost("/v1/cloud/ack",async(HttpContext c)=>{Rules.Need(c.Connection.LocalPort==receiverPort,"Usa listener mTLS",403);var cert=await c.Connection.GetClientCertificateAsync();Rules.Need(cert is not null,"Certificado requerido",403);return service.AckCloud(cert!,await Body(c));});
        app.MapGet("/v1/releases/latest",async(HttpContext c)=>{Rules.Need(c.Connection.LocalPort==receiverPort,"mTLS requerido",403);var cert=await c.Connection.GetClientCertificateAsync();Rules.Need(cert is not null,"mTLS requerido",403);return service.Latest(cert!,c.Request.Query["platform"].ToString());});
        app.MapGet("/v1/releases/{filename}",async(string filename,HttpContext c)=>{Rules.Need(c.Connection.LocalPort==receiverPort,"mTLS requerido",403);var cert=await c.Connection.GetClientCertificateAsync();Rules.Need(cert is not null,"mTLS requerido",403);return Results.File(service.Download(cert!,filename),"application/octet-stream",enableRangeProcessing:true);});
        app.MapPut("/api/packages/{filename}",async(string filename,HttpContext c)=>{
            service.AuthorizeUpload(Token(c));filename=ProviderService.PackageName(filename);Directory.CreateDirectory(packages);var path=Path.Combine(packages,filename);Rules.Need(!File.Exists(path),"Paquete existente",409);var tmp=path+"."+J.Id()+".upload";
            try{await using(var file=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.WriteThrough)){var buffer=new byte[65536];long total=0;int n;while((n=await c.Request.Body.ReadAsync(buffer))>0){total+=n;Rules.Need(total<=500L*1024*1024,"Paquete demasiado grande",413);await file.WriteAsync(buffer.AsMemory(0,n));}Rules.Need(total>10,"Paquete vacío");file.Flush(true);}File.Move(tmp,path,false);return J.O("filename",filename,"bytes",new FileInfo(path).Length);}finally{if(File.Exists(tmp))File.Delete(tmp);}
        });return app;
    }
}
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try{string? Arg(string key)=>args.SkipWhile(x=>x!="--"+key).Skip(1).FirstOrDefault();var credentials=Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");var directory=Arg("directory")??"/var/lib/mesanexo-central";var provider=Arg("provider-id")??throw new Exception();var keyFile=Arg("data-key")??Path.Combine(credentials??throw new Exception(),"central-data-key");using var store=new CentralStore(Path.Combine(directory,"provider.sqlite"),await File.ReadAllBytesAsync(keyFile));store.Initialize();if(Arg("snapshot-to") is string output){Console.WriteLine(store.Snapshot(output));return 0;}Directory.CreateDirectory(directory);using var processLock=new FileStream(Path.Combine(directory,"service.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);if(Arg("restore-from") is string backup){Rules.Need(Arg("confirm")=="RESTAURAR","Usa --confirm RESTAURAR");var before=store.Restore(backup,Arg("snapshot-sha256")??throw new Exception(),provider);Console.WriteLine("Restaurado y sesiones invalidadas. Copia previa: "+before);return 0;}var packages=Path.Combine(directory,"packages");var service=new ProviderService(store,new Passwords(),provider,packages,Arg("signing-key")??(credentials is null?null:Path.Combine(credentials,"release-signing-key")));
            if(Arg("provision-tenant") is string tenantConfig){store.ProvisionTenant(JsonNode.Parse(await File.ReadAllTextAsync(tenantConfig))!.AsObject(),new Passwords());Console.WriteLine("Empresa y dueño aprovisionados; retira el archivo de secretos.");return 0;}
            if(Arg("initialize") is string config){service.Initialize(JsonNode.Parse(await File.ReadAllTextAsync(config))!.AsObject());Console.WriteLine("Proveedor inicializado; retira el archivo de credenciales.");return 0;}
            using var certificate=X509Certificate2.CreateFromPemFile(Arg("tls-cert")??Path.Combine(credentials!,"tls-cert"),Arg("tls-key")??Path.Combine(credentials!,"tls-key"));await using var app=CentralHost.Build(service,provider,packages,certificate,int.Parse(Arg("port")??"9443"),int.Parse(Arg("receiver-port")??"9444"),Arg("bind")??"0.0.0.0");await app.RunAsync();return 0;
        }catch(BusinessException e){Console.Error.WriteLine(e.Message);return 1;}catch{Console.Error.WriteLine("No se pudo iniciar el servicio central: verifica configuración y credenciales.");return 1;}
    }
}