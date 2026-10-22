using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Api;
public sealed record PolicyOptions(string Url,string CredentialsDirectory,string PublicKeyFile);
// Signed business configuration pull. Python syncworker remains the sole outbox sender.
public sealed class PolicyRefresh(BranchService branch,PolicyOptions options):BackgroundService
{
    public string State{get;private set;}="unverified";
    public async Task<bool> Refresh(CancellationToken ct=default)
    {
        var uri=new Uri(options.Url);Rules.Need(uri.Scheme=="https"&&uri.UserInfo==""&&uri.Query==""&&uri.Fragment=="","La contratación central requiere HTTPS");var credentials=options.CredentialsDirectory;using var root=X509CertificateLoader.LoadCertificateFromFile(Path.Combine(credentials,"central-ca"));using var pem=X509Certificate2.CreateFromPemFile(Path.Combine(credentials,"central-cert"),Path.Combine(credentials,"central-key"));using var clientCert=X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx),null);using var handler=new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false};handler.SslOptions=new SslClientAuthenticationOptions{ClientCertificates=new X509CertificateCollection{clientCert},CertificateChainPolicy=new X509ChainPolicy{TrustMode=X509ChainTrustMode.CustomRootTrust,CustomTrustStore={root},RevocationMode=X509RevocationMode.NoCheck}};using var http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(4)};var raw=await http.GetStringAsync(uri,ct);Rules.Need(raw.Length<=65536,"Contratación demasiado grande");var envelope=JsonNode.Parse(raw)!.AsObject();
        try{await branch.ApplyEntitlement(envelope,options.PublicKeyFile,ct);State="available";branch.NoteCentral(State);return true;}catch(BusinessException e)when(e.Status==409){State="available";branch.NoteCentral(State);return false;}
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();while(!stoppingToken.IsCancellationRequested){try{await Refresh(stoppingToken);}catch(Exception)when(!stoppingToken.IsCancellationRequested){State="unavailable";branch.NoteCentral(State);}try{await Task.Delay(TimeSpan.FromSeconds(5),stoppingToken);}catch(OperationCanceledException){break;}}
    }
}