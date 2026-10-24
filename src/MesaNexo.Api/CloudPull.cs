using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Api;

public sealed record CloudOptions(string Url, string CredentialsDirectory, string PublicKeyFile);
// Outgoing connections only. Does not send the branch outbox or acknowledge unaccepted proposals.
public sealed class CloudPull(BranchService branch, CloudOptions options): BackgroundService {
    public async Task<int> Refresh(CancellationToken ct = default) {

        var uri = new Uri(options.Url);
        Rules.Need(uri.Scheme == "https" && uri.UserInfo == "" && uri.Query == "" && uri.Fragment == "", "Central HTTPS requerida");
        var dir = options.CredentialsDirectory;
        using var root = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(dir, "central-ca"));
        using var pem = X509Certificate2.CreateFromPemFile(Path.Combine(dir, "central-cert"), Path.Combine(dir, "central-key"));
        using var cert = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx), null);
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        handler.SslOptions = new SslClientAuthenticationOptions { ClientCertificates = new X509CertificateCollection { cert }, CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, CustomTrustStore = { root }, RevocationMode = X509RevocationMode.NoCheck, ApplicationPolicy = { new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1") } } };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await http.GetAsync(new Uri(uri, "/v1/cloud/pending"), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int n;
        while ((n = await input.ReadAsync(bytes, ct)) > 0) { Rules.Need(buffer.Length + n <= 64 * 1024, "Entrega demasiado grande", 413); buffer.Write(bytes, 0, n); }
        var envelope = JsonNode.Parse(buffer.ToArray())!.AsObject();
        var acks = await branch.ReceiveCloudProposals(envelope, options.PublicKeyFile, ct);
        foreach (var ack in acks) { using var result = await http.PostAsync(new Uri(uri, "/v1/cloud/ack"), new StringContent(J.O("id", ack.S("id"), "ack", ack).ToJsonString(), Encoding.UTF8, "application/json"), ct); result.EnsureSuccessStatusCode(); }
        branch.CloudState = "available";
        return acks.Length;

    }
    protected override async Task ExecuteAsync(CancellationToken ct) { await Task.Yield(); while (!ct.IsCancellationRequested) { try { await Refresh(ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch { branch.CloudState = "unavailable"; } try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; } } }
}
