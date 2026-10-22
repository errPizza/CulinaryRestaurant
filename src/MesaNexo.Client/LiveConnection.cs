using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.SignalR.Client;
namespace MesaNexo.Client;
public sealed partial class NativeSession
{
    HubConnection? live;public string LiveState=>live?.State.ToString()??"Stopped";
    public async Task StartLive(Func<Task> refresh,CancellationToken ct=default)
    {
        if(live is not null)return;var root=X509CertificateLoader.LoadCertificateFromFile(profile.CaFile);
        live=new HubConnectionBuilder().WithUrl(profile.ServerUrl.TrimEnd('/')+"/live",o=>{
            o.AccessTokenProvider=()=>Task.FromResult<string?>(Token);o.HttpMessageHandlerFactory=_=>TrustedConnection.Handler(profile);
            o.WebSocketConfiguration=ws=>{ws.RemoteCertificateValidationCallback=(_,certificate,_,errors)=>{
                if(certificate is null||(errors&SslPolicyErrors.RemoteCertificateNameMismatch)!=0)return false;using var chain=new X509Chain();chain.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;chain.ChainPolicy.CustomTrustStore.Add(root);chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));return chain.Build(new X509Certificate2(certificate));};};
        }).WithAutomaticReconnect([TimeSpan.Zero,TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(15)]).Build();
        live.On<long>("revision",async _=>await refresh());live.Reconnected+=async _=>await refresh();await live.StartAsync(ct);await refresh();
    }
    public async Task<System.Text.Json.Nodes.JsonObject> LiveSnapshot(CancellationToken ct=default)=>System.Text.Json.Nodes.JsonNode.Parse(await live!.InvokeAsync<string>("Snapshot",ct))!.AsObject();
    public async Task StopLive(){if(live is not null){await live.DisposeAsync();live=null;}}
}
