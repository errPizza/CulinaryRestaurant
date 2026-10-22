using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using MesaNexo.Central;
using MesaNexo.Core;
using Microsoft.AspNetCore.RateLimiting;
namespace MesaNexo.Public;
// Explicit separate process. No private branch routes, branch DB, keyring or production deployment.
public static class PublicHost
{
    public static WebApplication Build(CommerceService service,X509Certificate2 certificate,int port,string webroot)
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{Args=[],WebRootPath=webroot,ContentRootPath=Path.GetDirectoryName(webroot)!});builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(k=>{k.Limits.MaxRequestBodySize=64*1024;k.Listen(IPAddress.Loopback,port,l=>l.UseHttps(certificate));});
        builder.Services.AddRateLimiter(o=>o.AddPolicy("public",ctx=>RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions{PermitLimit=10,Window=TimeSpan.FromMinutes(1),QueueLimit=0})));
        var app=builder.Build();app.Use(async(c,next)=>{
            c.Response.Headers["X-Content-Type-Options"]="nosniff";c.Response.Headers["Referrer-Policy"]="no-referrer";c.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: https:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
            if(c.Request.Path.StartsWithSegments("/public"))c.Response.Headers.CacheControl="no-store";
            try{var origin=c.Request.Headers.Origin.ToString();Rules.Need(origin==""||origin=="https://"+c.Request.Host,"Origen no permitido",403);await next();}catch(BusinessException e){c.Response.StatusCode=e.Status;await c.Response.WriteAsJsonAsync(J.O("error",e.Message));}catch(Exception e)when(e is System.Text.Json.JsonException or FormatException or InvalidOperationException or OverflowException){c.Response.StatusCode=400;await c.Response.WriteAsJsonAsync(J.O("error","Solicitud inválida"));}catch{c.Response.StatusCode=503;await c.Response.WriteAsJsonAsync(J.O("error","Servicio no disponible; conserva tu solicitud para consultar o reintentar"));}
        });app.UseRateLimiter();app.UseStaticFiles();
        static async Task<JsonObject> Body(HttpContext c)=>(await JsonNode.ParseAsync(c.Request.Body))?.AsObject()??throw new BusinessException(400,"Solicitud JSON requerida");
        app.MapGet("/public/{slug}/manifest",(string slug)=>Results.Json(J.O("name","Culinary Restaurant","short_name","Culinary","start_url","/s/"+Uri.EscapeDataString(slug),"display","standalone","theme_color","#202733","background_color","#e7e9ed","icons",new[]{J.O("src","/icon-256.png","sizes","256x256","type","image/png")})));
        app.MapGet("/public/{slug}/catalog",(string slug)=>service.Catalog(slug));
        app.MapPost("/public/{slug}/orders",async(string slug,HttpContext c)=>service.Checkout(slug,await Body(c))).RequireRateLimiting("public");
        app.MapGet("/public/{slug}/orders/{id}",(string slug,string id,HttpContext c)=>{var h=c.Request.Headers.Authorization.ToString();Rules.Need(h.StartsWith("Bearer "),"Token requerido",401);return service.Status(slug,id,h[7..]);}).RequireRateLimiting("public");
        app.MapGet("/public/{slug}/table/{token}",(string slug,string token)=>service.TableInfo(slug,token));
        app.MapPost("/public/{slug}/table/{token}",async(string slug,string token,HttpContext c)=>service.TableRequest(slug,token,await Body(c))).RequireRateLimiting("public");
        app.MapPost("/public/invoice",async(HttpContext c)=>{var p=await Body(c);return service.Invoice(p.S("token"),p.Obj("receiver"));}).RequireRateLimiting("public");
        app.MapGet("/s/{slug}",()=>Results.File(Path.Combine(webroot,"index.html"),"text/html"));app.MapGet("/invoice",()=>Results.File(Path.Combine(webroot,"index.html"),"text/html"));return app;
    }
}
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? Arg(string k)=>args.SkipWhile(x=>x!="--"+k).Skip(1).FirstOrDefault();try{
            var db=Arg("database")??throw new Exception();Rules.Need(File.Exists(db),"Se requiere una central previamente aprovisionada");var key=await File.ReadAllBytesAsync(Arg("data-key")??throw new Exception());using var store=new CentralStore(db,key);store.Initialize();using var cert=X509Certificate2.CreateFromPemFile(Arg("tls-cert")??throw new Exception(),Arg("tls-key")??throw new Exception());await using var app=PublicHost.Build(new CommerceService(store),cert,int.Parse(Arg("port")??"9445"),Path.Combine(AppContext.BaseDirectory,"wwwroot"));await app.RunAsync();return 0;
        }catch{Console.Error.WriteLine("Portal no iniciado: configura central, clave propia y TLS. Escucha sólo loopback; publicación externa requiere proxy administrado.");return 1;}
    }
}
