using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Integrations;
// The guest staying in a room, and the result of posting a charge to their folio.
public sealed record PmsGuest(string ReservationId,string Name,string SubReservationId="");
public sealed record PmsPosting(string Status,string Reference="",string Message="");
public interface IPmsAdapter
{
    string System{get;}
    Task<PmsGuest?> InHouse(string room,CancellationToken ct);
    // `reference` is the posting's own id: repeating it after a lost answer never posts twice.
    Task<PmsPosting> Post(PmsGuest guest,string room,long cents,string reference,string itemName,CancellationToken ct);
    // Whether a posting with this reference reached the folio, for an answer that was lost.
    Task<PmsPosting?> Find(PmsGuest guest,string reference,CancellationToken ct);
}
static class Pesos{public static decimal Of(long cents)=>cents/100m;public static string Text(long cents)=>Of(cents).ToString("0.00",CultureInfo.InvariantCulture);}
// Cloudbeds API v1.3 (verified 2026-10-23 at developers.cloudbeds.com) with an API key (x-api-key) holding read:reservation and
// write:item. postCustomItem refuses a repeated referenceID, so posting again with the same reference is safe.
public sealed class CloudbedsPms(HttpClient http,string apiKey,string propertyId):IPmsAdapter
{
    public string System=>"cloudbeds";
    async Task<JsonNode?> Call(HttpRequestMessage request,CancellationToken ct)
    {
        request.Headers.Add("x-api-key",apiKey);using var response=await http.SendAsync(request,ct);var text=await response.Content.ReadAsStringAsync(ct);
        if((int)response.StatusCode>=500||(int)response.StatusCode==429)throw new HttpRequestException("Cloudbeds HTTP "+(int)response.StatusCode);
        if(response.StatusCode is global::System.Net.HttpStatusCode.Unauthorized or global::System.Net.HttpStatusCode.Forbidden)throw new BusinessException(400,"Cloudbeds rechazó la llave de API");
        try{return JsonNode.Parse(text);}catch(global::System.Text.Json.JsonException){throw new HttpRequestException("Respuesta ilegible de Cloudbeds");}
    }
    public async Task<PmsGuest?> InHouse(string room,CancellationToken ct)
    {
        var data=await Call(new(HttpMethod.Get,"https://api.cloudbeds.com/api/v1.3/getReservations?propertyID="+Uri.EscapeDataString(propertyId)+"&status=checked_in&roomName="+Uri.EscapeDataString(room)+"&includeGuestsDetails=true"),ct);
        var first=data.A("data").Objects().FirstOrDefault();if(first is null)return null;
        var guests=first.Obj("guestList").Select(x=>x.Value as JsonObject).Where(x=>x is not null).Select(x=>(x!.S("guestFirstName")+" "+x.S("guestLastName")).Trim()).Where(x=>x!="").ToArray();
        return new(first.S("reservationID"),guests.Length>0?string.Join(" / ",guests):first.S("guestName"),first.S("subReservationID"));
    }
    public async Task<PmsPosting> Post(PmsGuest guest,string room,long cents,string reference,string itemName,CancellationToken ct)
    {
        var form=new Dictionary<string,string>{["propertyID"]=propertyId,["reservationID"]=guest.ReservationId,["referenceID"]=reference,["items[0][appItemID]"]="culinary-restaurant",["items[0][itemName]"]=itemName,["items[0][itemQuantity]"]="1",["items[0][itemPrice]"]=Pesos.Text(cents),["items[0][itemNote]"]="Habitación "+room+" · "+reference};
        if(guest.SubReservationId!="")form["subReservationID"]=guest.SubReservationId;
        var data=await Call(new(HttpMethod.Post,"https://api.cloudbeds.com/api/v1.3/postCustomItem"){Content=new FormUrlEncodedContent(form)},ct);
        return data?["success"]?.GetValue<bool>()==true?new("posted",data.Obj("data").S("soldProductID") is {Length:>0} sold?sold:reference,data.Obj("data").S("notice") is {Length:>0} n?"Cargo ya registrado en Cloudbeds":"Cargo registrado en Cloudbeds"):new("rejected",Message:"Cloudbeds rechazó el cargo: "+data.S("message"));
    }
    public async Task<PmsPosting?> Find(PmsGuest guest,string reference,CancellationToken ct)=>null;
}
// Oracle OPERA Cloud through OHIP (paths from github.com/oracle/hospitality-api-docs, verified 2026-10-23): the in-house
// reservation of a room from rsv/v1, the charge to its folio with csh/v1 postBillingCharges and the transaction code the hotel
// gives the restaurant. The token comes from the OAuth endpoint the hotel's OHIP environment uses.
public sealed class OperaPms(HttpClient http,OAuthToken token,string gatewayUrl,string appKey,string hotelId,string transactionCode,long? cashierId):IPmsAdapter
{
    public string System=>"opera";
    async Task<(int Code,JsonNode? Body)> Call(HttpMethod method,string path,JsonObject? body,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(method,gatewayUrl.TrimEnd('/')+"/"+path);request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",await token.Get(ct));request.Headers.Add("x-app-key",appKey);request.Headers.Add("x-hotelid",hotelId);
        if(body is not null)request.Content=new StringContent(body.ToJsonString(),Encoding.UTF8,"application/json");
        using var response=await http.SendAsync(request,ct);var text=await response.Content.ReadAsStringAsync(ct);JsonNode? data=null;try{data=text==""?null:JsonNode.Parse(text);}catch(global::System.Text.Json.JsonException){}
        if((int)response.StatusCode>=500||(int)response.StatusCode==429)throw new HttpRequestException("OHIP HTTP "+(int)response.StatusCode);
        if((int)response.StatusCode==401){token.Reset();throw new HttpRequestException("OHIP rechazó el token");}
        return ((int)response.StatusCode,data);
    }
    static string Error(JsonNode? data,int code)=>data.S("detail") is {Length:>0} d?d:data.S("title") is {Length:>0} t?t:"HTTP "+code;
    public async Task<PmsGuest?> InHouse(string room,CancellationToken ct)
    {
        var (code,data)=await Call(HttpMethod.Get,"rsv/v1/hotels/"+Uri.EscapeDataString(hotelId)+"/reservations?roomId="+Uri.EscapeDataString(room)+"&reservationStatuses=InHouse&limit=5",null,ct);
        if(code>=300)throw new BusinessException(400,"OPERA no respondió la búsqueda: "+Error(data,code));
        var info=data.Obj("reservations").A("reservationInfo").Objects().FirstOrDefault();if(info is null)return null;
        var id=info.A("reservationIdList").Objects().FirstOrDefault(x=>x.S("type")=="Reservation")?.S("id")??info.A("reservationIdList").Objects().FirstOrDefault()?.S("id")??"";var g=info.Obj("reservationGuest");
        return new(id,(g.S("givenName")+" "+g.S("surname")).Trim());
    }
    public async Task<PmsPosting> Post(PmsGuest guest,string room,long cents,string reference,string itemName,CancellationToken ct)
    {
        var charge=J.O("transactionCode",transactionCode,"price",J.O("amount",Pesos.Of(cents),"currencyCode","MXN"),"postingQuantity",1,"postingReference",reference,"postingRemark",itemName+" · habitación "+room);if(cashierId is {} cashier)charge["cashierId"]=cashier;
        var criteria=J.O("hotelId",hotelId,"reservationId",J.O("type","Reservation","id",guest.ReservationId),"charges",new JsonArray(charge));if(cashierId is {} c)criteria["cashierId"]=J.O("id",c);
        var (code,data)=await Call(HttpMethod.Post,"csh/v1/hotels/"+Uri.EscapeDataString(hotelId)+"/reservations/"+Uri.EscapeDataString(guest.ReservationId)+"/charges",J.O("criteria",criteria),ct);
        return code<300?new("posted",reference,"Cargo registrado en OPERA"):new("rejected",Message:"OPERA rechazó el cargo: "+Error(data,code));
    }
    public async Task<PmsPosting?> Find(PmsGuest guest,string reference,CancellationToken ct)
    {
        var (code,data)=await Call(HttpMethod.Get,"csh/v1/hotels/"+Uri.EscapeDataString(hotelId)+"/reservations/"+Uri.EscapeDataString(guest.ReservationId)+"/charges",null,ct);if(code>=300)return null;
        // The posting's reference appears in the folio's postings; any shape of the answer is searched for it.
        return data?.ToJsonString().Contains("\""+reference+"\"",StringComparison.Ordinal)==true?new("posted",reference,"Cargo encontrado en el folio de OPERA"):new("absent");
    }
}
// An OAuth 2.0 access token kept until a minute before it expires: client credentials, or the password grant with an
// integration user, as the OHIP environment is set up.
public sealed class OAuthToken(HttpClient http,string tokenUrl,string clientId,string clientSecret,string grantType,string scope="",string username="",string password="",string appKey="")
{
    string value="";DateTimeOffset until;readonly SemaphoreSlim gate=new(1,1);
    public void Reset()=>until=DateTimeOffset.MinValue;
    public async Task<string> Get(CancellationToken ct)
    {
        await gate.WaitAsync(ct);try{
            if(value!=""&&DateTimeOffset.UtcNow<until)return value;
            var form=new Dictionary<string,string>{["grant_type"]=grantType};if(scope!="")form["scope"]=scope;if(grantType=="password"){form["username"]=username;form["password"]=password;}
            using var request=new HttpRequestMessage(HttpMethod.Post,tokenUrl){Content=new FormUrlEncodedContent(form)};request.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId+":"+clientSecret)));if(appKey!="")request.Headers.Add("x-app-key",appKey);
            using var response=await http.SendAsync(request,ct);var data=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            Need(response.IsSuccessStatusCode&&data.S("access_token")!="","El servidor OAuth rechazó las credenciales de OHIP");
            value=data.S("access_token");until=DateTimeOffset.UtcNow.AddSeconds(Math.Max(60,data.L("expires_in",3600))-60);return value;
        }finally{gate.Release();}
    }
}
