using System.Text.Json.Nodes;
using MesaNexo.Core;
using MesaNexo.Integrations;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
// pms.json (example in deploy/integrations/pms.example.json): "system": "cloudbeds" or "opera", with
//   "cloudbeds": {apiKey, propertyId}
//   "opera": {gatewayUrl, tokenUrl, grantType ("client_credentials" or "password"), clientId, clientSecret, scope, username,
//             password, appKey, hotelId, transactionCode, cashierId}
// plus "creditLimit" (MXN per room within a week, 0 = the hotel decides) and "itemName" (what the folio shows).
public sealed class PmsService(IPmsAdapter pms,long creditLimit,string itemName)
{
    public static PmsService From(JsonObject o)
    {
        var system=Choice(o,"system","cloudbeds","opera");var http=new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(20)};
        IPmsAdapter adapter;
        if(system=="cloudbeds"){var c=o.Obj("cloudbeds");adapter=new CloudbedsPms(http,Text(c,"apiKey",300),Text(c,"propertyId",40));}
        else{
            var c=o.Obj("opera");var gateway=new Uri(Text(c,"gatewayUrl",300));var tokenUrl=new Uri(Text(c,"tokenUrl",300));Need(gateway.Scheme=="https"&&tokenUrl.Scheme=="https","opera.gatewayUrl y opera.tokenUrl: direcciones HTTPS");
            var grant=Choice(c,"grantType","client_credentials","password");var appKey=Text(c,"appKey",100);
            var token=new OAuthToken(http,tokenUrl.ToString(),Text(c,"clientId",200),Text(c,"clientSecret",300),grant,c.S("scope"),grant=="password"?Text(c,"username",100):"",grant=="password"?Text(c,"password",200):"",appKey);
            adapter=new OperaPms(http,token,gateway.ToString(),appKey,Text(c,"hotelId",40),Text(c,"transactionCode",20),c.ContainsKey("cashierId")?c.L("cashierId"):null);
        }
        var limit=o.L("creditLimit");Need(limit>=0,"creditLimit no puede ser negativo");var item=o.S("itemName");
        return new(adapter,limit*100,item==""?"Consumo en restaurante":item);
    }
    string Name=>pms.System=="opera"?"OPERA":"Cloudbeds";
    JobResult Rejected(JsonObject post,string message)=>JobResult.Of("rejected","Habitación "+post.S("room")+" · "+message,"",("message",message),("system",pms.System));
    // Looks up who is staying in the room, checks the surname and the credit, then posts the charge.
    public async Task<JobResult> Send(BranchState s,JsonObject post,CancellationToken ct)
    {
        var room=post.S("room");var guest=await pms.InHouse(room,ct);
        if(guest is null)return Rejected(post,Name+" no tiene un huésped hospedado en la habitación "+room);
        if(!Restaurant.SameGuest(guest.Name,post.S("guestGiven")))return Rejected(post,"El apellido no coincide con el huésped de la habitación "+room);
        if(creditLimit>0){
            var since=DateTimeOffset.UtcNow.AddDays(-7);var used=s.Related("pmspost","room",room).Where(x=>x.S("status") is "posted" or "posted-unapplied"&&DateTimeOffset.TryParse(x.S("at"),out var at)&&at>since).Sum(x=>x.L("amount"));
            if(used+post.L("amount")>creditLimit)return Rejected(post,"Supera el crédito autorizado de la habitación: quedan $"+(Math.Max(0,creditLimit-used)/100m).ToString("0.00",System.Globalization.CultureInfo.InvariantCulture));
        }
        return Result(post,guest,await pms.Post(guest,room,post.L("amount"),post.S("id"),itemName,ct));
    }
    JobResult Result(JsonObject post,PmsGuest guest,PmsPosting posting)=>posting.Status=="posted"
        ?JobResult.Of("posted",Name+" · cargo a habitación "+post.S("room")+" · "+(post.L("amount")/100m).ToString("0.00",System.Globalization.CultureInfo.InvariantCulture),posting.Reference,("reference",posting.Reference),("guest",guest.Name),("reservationId",guest.ReservationId),("system",pms.System),("message",posting.Message))
        :Rejected(post,posting.Message);
    // Cloudbeds refuses a repeated reference, so the posting is simply sent again; OPERA's folio is searched for it first.
    public async Task<JobResult?> Recover(BranchState s,JsonObject post,CancellationToken ct)
    {
        if(pms.System=="cloudbeds"||post.S("reservationId")=="")return await Send(s,post,ct);
        var guest=new PmsGuest(post.S("reservationId"),post.S("guest"));var found=await pms.Find(guest,post.S("id"),ct);
        return found?.Status=="posted"?Result(post,guest,found):found?.Status=="absent"?await Send(s,post,ct):null;
    }
}
public static partial class LiveServices
{
    static void Pms(IServiceCollection services,IntegrationConfig config)
    {
        if(config.Use("pms",PmsService.From) is not {} pms)return;
        Worker(services,"pms",branch=>ct=>branch.SendNext("pmspost","pms-service",x=>x.S("status")=="queued",pms.Send,pms.Recover,ct));
    }
}
