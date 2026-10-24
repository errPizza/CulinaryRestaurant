using MesaNexo.Core;
using Microsoft.AspNetCore.SignalR;
namespace MesaNexo.Api;
// One authority/site per process. Hints are scoped to an authenticated site group.
// Every snapshot reauthenticates the user, role, device and session in durable storage.
public sealed class LiveHub(BranchService service): Hub {
    public static string SiteGroup(string site) => "authorized-site:" + site;
    string Token() { var c = Context.GetHttpContext()!; var h = c.Request.Headers.Authorization.ToString(); var token = h.StartsWith("Bearer ") ? h[7..] : c.Request.Query["access_token"].ToString(); Rules.Need(token.Length is > 10 and < 200, "Sesión requerida", 401); return token; }
    public override async Task OnConnectedAsync() { try { var state = await service.State(Token(), Context.ConnectionAborted); Rules.Need(state.S("siteId") == service.SiteId, "Sucursal incorrecta", 403); await Groups.AddToGroupAsync(Context.ConnectionId, SiteGroup(service.SiteId), Context.ConnectionAborted); await base.OnConnectedAsync(); } catch { Context.Abort(); throw new HubException("Sesión o dispositivo no autorizado"); } }
    public async Task<string> Snapshot() { try { return (await service.State(Token(), Context.ConnectionAborted)).ToJsonString(); } catch { await Groups.RemoveFromGroupAsync(Context.ConnectionId, SiteGroup(service.SiteId)); Context.Abort(); throw new HubException("Sesión no autorizada"); } }
}
