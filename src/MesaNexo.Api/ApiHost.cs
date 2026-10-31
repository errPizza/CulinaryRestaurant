using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using MesaNexo.Core;
namespace MesaNexo.Api;

public sealed record ApiOptions(string SiteId, int Port = 8443, string Bind = "0.0.0.0", string Socket = "/run/culinaryos-data/store.sock", string? Credentials = null, string? LabPython = null, string? LabScript = null, string? LabDirectory = null, string? ReadySocket = "/run/culinaryos-app/ready.sock", string? SetupCode = null);
public static class ApiHost {
    public static WebApplication Build(ApiOptions options, IBranchStore store, X509Certificate2 cert, LicenseOptions? licensing = null, CloudOptions? cloud = null, IPrintDriver? printer = null, FiscalOptions? fiscal = null, IntegrationConfig? integrations = null, ObjectBucket? checkpoints = null) {

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions{ Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(x => x.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = 112 * 1024; k.Listen(IPAddress.Parse(options.Bind), options.Port, l => l.UseHttps(cert)); });
        builder.Services.AddSingleton<IBranchStore>(store);
        builder.Services.AddSingleton<IPasswordHasher, Passwords>();
        builder.Services.AddSingleton(sp => new BranchService(store, options.SiteId, sp.GetRequiredService<IPasswordHasher> ()));
        builder.Services.AddRateLimiter(o => o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _=> new FixedWindowRateLimiterOptions{ PermitLimit = 8, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }))); 
       
        if (cloud is not null) builder.Services.AddSingleton<IHostedService>(sp => new CloudPull(sp.GetRequiredService<BranchService> (), cloud));
        if (licensing?.ServerUrl is not null) builder.Services.AddSingleton<IHostedService>(sp => new LicenseWorker(sp.GetRequiredService<BranchService> (), sp.GetRequiredService<ILogger<LicenseWorker>> ()));
       
        if (checkpoints is not null && licensing is not null) builder.Services.AddSingleton<IHostedService>(sp => new CheckpointWorker(sp.GetRequiredService<BranchService> (), checkpoints, sp.GetRequiredService<ILogger<CheckpointWorker>> ()));
        builder.Services.AddSignalR(o => { o.MaximumReceiveMessageSize = 16 * 1024; o.EnableDetailedErrors = false; });
        builder.Services.AddHostedService<InventoryWorker> (); builder.Services.AddHostedService<OutboxWorker> ();
        if (store is PostgresBranchStore branchStore && options.Credentials is not null) builder.Services.AddSingleton<IHostedService> (sp => new CentralSyncWorker(branchStore, "/etc/culinaryos/branch.json", options.Credentials, sp.GetRequiredService<ILogger<CentralSyncWorker>> ()));
        
        if (printer is not null) builder.Services.AddSingleton<IHostedService> (sp => new PrintWorker(sp.GetRequiredService<BranchService> (), printer, sp.GetRequiredService<ILogger<PrintWorker>> ()));
        if (fiscal is not null) builder.Services.AddSingleton<IHostedService> (sp => new FiscalWorker(sp.GetRequiredService<BranchService> (), fiscal.Adapter(), fiscal.Issuer, sp.GetRequiredService<ILogger<FiscalWorker>> (), fiscal.EmailReceiver));
        
        integrations ??= new();
        
        if (printer is not null) integrations.Ready("printer");
        if (fiscal is not null) integrations.Ready("pac");

        var terminals = LiveServices.Register(builder.Services, integrations);
        var app = builder.Build();
        var service = app.Services.GetRequiredService<BranchService> ();
        service.UseIntegrations(integrations, terminals);
        service.UseCertificate(cert.NotAfter);
        if (licensing is not null) service.UseLicensing(licensing);

        app.Use(async(ctx, next) => {

            ctx.Response.Headers.CacheControl = "no-store"; 
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";

            try{ await next(); } catch(BusinessException e){ await Error(ctx,e.Status,e.Message); } catch(StoreException e){ await Error(ctx, e.Code == "COS-DATA-CONFLICT" || e.Code == "COS-DATA-IDEMPOTENCY" ? 409: e.Code == "COS-DATA-INVALID" ? 413: e.Code == "COS-DATA-FULL" ? 507: 503, e.Code == "COS-DATA-INVALID" ? "Carga demasiado grande o inválida; divide la comanda o reduce el tamaño de la imagen.": e.Code); } catch(Exception e) when(e is JsonException or FormatException or InvalidOperationException or OverflowException){ await Error(ctx, 400, "Solicitud o formato inválido"); } catch(Exception){ await Error(ctx, 503, "Servicio local no disponible; conserva la operación y reconcilia"); }
        
        });

        app.UseRateLimiter();
        app.MapHub<LiveHub>("/live");
        var hub = app.Services.GetRequiredService<Microsoft.AspNetCore.SignalR.IHubContext<LiveHub>> ();
        service.RevisionChanged += revision => { _ = hub.Clients.Group(LiveHub.SiteGroup(service.SiteId)).SendCoreAsync("revision", new object?[]{ revision }); };
        app.MapGet("/api/identity", () => J.O("name", "Culinary Restaurant", "siteId", options.SiteId, "protocol", 1));
        app.MapGet("/api/health", async(CancellationToken ct) => await service.Ready(ct) ? Results.Json(J.O("ready", true, "site_id", options.SiteId)): Results.StatusCode(503));
        app.MapPost("/api/login", async(HttpContext c, CancellationToken ct) => { var p = await Body(c, ct); Rules.Need(p.S("siteId") == options.SiteId, "Sucursal equivocada", 403); return await service.Login(p, ct); }).RequireRateLimiting("login");
        // Before sign-in: the lock screen reads the license state, and a locked branch accepts a new activation.
        app.MapGet("/api/license", async(CancellationToken ct) => await service.LicenseStatus(ct));
        app.MapPost("/api/license/activate", async(HttpContext c, CancellationToken ct) => await service.Activate(await Body(c, ct), ct)).RequireRateLimiting("login");
        app.MapGet("/api/setup", async(CancellationToken ct) => J.O("needsSetup", options.SetupCode is not null && await service.AwaitingSetup(ct), "siteId", options.SiteId));
        app.MapPost("/api/setup", async(HttpContext c, CancellationToken ct) => await service.Setup(await Body(c, ct), options.SetupCode, ct)).RequireRateLimiting("login");
        app.MapPost("/api/updates/check", async(HttpContext c, CancellationToken ct) => await service.CheckUpdate(await Body(c, ct), ct)).RequireRateLimiting("login");
        app.MapPost("/api/updates/download", async(HttpContext c, CancellationToken ct) => { var response = await service.DownloadUpdate(await Body(c, ct), ct); c.Response.RegisterForDispose(response); return Results.Stream(await response.Content.ReadAsStreamAsync(ct), "application/octet-stream"); });
        app.MapPost("/api/device/can-install", async(HttpContext c, CancellationToken ct) => await service.CanInstall(await Body(c, ct), ct));
        app.MapGet("/api/preview/{id}", async(string id, HttpContext c, CancellationToken ct) => await service.Preview(Token(c), id, ct));
        app.MapGet("/api/reports", async(HttpContext c, CancellationToken ct) => await service.Report(Token(c), c.Request.Query["from"], c.Request.Query["until"], c.Request.Query["after"], ct));
        app.MapGet("/api/audit/verify", async(HttpContext c, CancellationToken ct) => await service.VerifyAudit(Token(c), ct));
        app.MapGet("/api/state", async(HttpContext c, CancellationToken ct) => await service.State(Token(c), ct, c.Request.Query.TryGetValue("since", out var since) ? since.ToString(): null));
        app.MapPost("/api/action", async(HttpContext c, CancellationToken ct) => await service.Action(Token(c), await Body(c, ct), ct));
        app.MapGet("/api/operations/{id}", async(string id, HttpContext c, CancellationToken ct) => await service.OperationStatus(Token(c), id, ct));

        if (options.ReadySocket is not null){ 

            var readiness = new Readiness(service, options.ReadySocket, options.SetupCode is not null);
            app.Lifetime.ApplicationStarted.Register(() => readiness.Start());
            app.Lifetime.ApplicationStopping.Register(readiness.Dispose);
        
        }
       
        return app;
    }

    static string Token(HttpContext c){ var h = c.Request.Headers.Authorization.ToString(); Rules.Need(h.StartsWith("Bearer ") && h.Length is > 10 and < 200, "Autenticación requerida", 401); return h[7..]; }
    static async Task<JsonObject> Body(HttpContext c, CancellationToken ct) => (await JsonNode.ParseAsync(c.Request.Body, cancellationToken: ct)) as JsonObject ?? throw new BusinessException(400, "Objeto JSON requerido");
    static async Task Error(HttpContext c, int status, string message){ if (c.Response.HasStarted) return; c.Response.StatusCode = status; await c.Response.WriteAsJsonAsync(J.O("error", message, "status", status)); }

}
public sealed class Readiness(BranchService service, string path, bool setupMode = false): IDisposable {
    readonly CancellationTokenSource stop = new(); Socket? listener; bool bound;
    public void Start() { _ = Run(); }
    async Task Run() { try {

        await RemoveStaleSocket(); listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified); listener.Bind(new UnixDomainSocketEndPoint(path)); bound = true;
        
        if (!OperatingSystem.IsWindows())File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        
        listener.Listen(8);
        
        while (!stop.IsCancellationRequested) { using var client = await listener.AcceptAsync(stop.Token); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(1500); try{

            using var stream = new NetworkStream(client);
            var buffer = new byte[4096];
            var used = 0; 
            while(used < buffer.Length){ 

                var read = await stream.ReadAsync(buffer.AsMemory(used, 1), timeout.Token); 

                if (read == 0) break;

                used += read; if (buffer[used - 1] == 10) break;

            }
                
            var p = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, used));
            var valid = p?.S("op") == "readiness" && p.I("protocol") == 1;
            var active = valid && await service.Ready(timeout.Token);
            var setup = valid && !active && setupMode && await service.AwaitingSetup(timeout.Token);

            await stream.WriteAsync(Encoding.UTF8.GetBytes(J.O("ready", active || setup, "site_id", service.SiteId, "setup", setup).ToJsonString() + "\n"), timeout.Token);
        } catch(Exception) when(!stop.IsCancellationRequested) {} }

    } catch(Exception) when(stop.IsCancellationRequested) {} catch(Exception){ Console.Error.WriteLine("COS-APP-READINESS: no se pudo atender el socket reservado"); } }
    async Task RemoveStaleSocket() {

        if(!File.Exists(path)) return;
        if(new FileInfo(path).LinkTarget is not null) throw new IOException("Readiness path cannot be a symbolic link");

        var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/test") { UseShellExecute = false, CreateNoWindow = true};
        start.ArgumentList.Add("-S");
        start.ArgumentList.Add(path);
        using var check = System.Diagnostics.Process.Start(start)!; await check.WaitForExitAsync();
        
        if(check.ExitCode!= 0) throw new IOException("Readiness path occupied by a non-socket file");

        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var timeout = new CancellationTokenSource(1000);
        
        try {

            await probe.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token); 
            throw new IOException("Another readiness listener is active");

        } catch(SocketException e) when(e.SocketErrorCode == SocketError.ConnectionRefused){ File.Delete(path); }
    
    }

    public void Dispose(){ stop.Cancel(); listener?.Dispose(); if(bound)File.Delete(path); stop.Dispose(); }
}
public static class LabCertificate {
    public static X509Certificate2 Create(string directory) {

        Directory.CreateDirectory(directory); var certFile = Path.Combine(directory, "lab-server.pfx");
       
        if(File.Exists(certFile)) return X509CertificateLoader.LoadPkcs12FromFile(certFile, null);
       
        using var key = RSA.Create(3072); var req = new CertificateRequest("CN=Culinary Restaurant LAB only", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(7)); File.WriteAllBytes(certFile, cert.Export(X509ContentType.Pfx)); File.WriteAllText(Path.Combine(directory, "lab-ca.crt"), cert.ExportCertificatePem()); return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    
    }

}