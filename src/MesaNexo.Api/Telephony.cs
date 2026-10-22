using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
public sealed record CallerEvent(string CallId,string Number,string Name);
public static class AsteriskFrames
{
    public static CallerEvent? Parse(IReadOnlyDictionary<string,string> frame)=>frame.GetValueOrDefault("Event")=="NewCallerid"&&frame.GetValueOrDefault("Uniqueid") is string id&&frame.GetValueOrDefault("CallerIDNum") is string number&&number.Length is >0 and <=40?new(id,number,frame.GetValueOrDefault("CallerIDName")??""):null;
}
// AMI over TLS with OS-trusted CA and verified hostname. Admin credentials are server-only.
public sealed class AsteriskCallerId(string host,int port,string username,string secret)
{
    public async Task Listen(Func<CallerEvent,Task> receive,CancellationToken ct)
    {
        Need(host.Length>0&&port is >0 and <=65535&&new[]{username,secret}.All(x=>x.Length>0&&!x.Contains('\r')&&!x.Contains('\n')),"Configuración AMI inválida");using var client=new TcpClient();await client.ConnectAsync(host,port,ct);await using var tls=new SslStream(client.GetStream());await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost=host,EnabledSslProtocols=System.Security.Authentication.SslProtocols.Tls12|System.Security.Authentication.SslProtocols.Tls13},ct);using var writer=new StreamWriter(tls,new UTF8Encoding(false),leaveOpen:true){NewLine="\r\n",AutoFlush=true};using var reader=new StreamReader(tls,Encoding.UTF8,leaveOpen:true);
        await writer.WriteAsync(("Action: Login\r\nUsername: "+username+"\r\nSecret: "+secret+"\r\nEvents: call\r\n\r\n").AsMemory(),ct);var fields=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);int size=0;bool authenticated=false;
        while(!ct.IsCancellationRequested){var line=await reader.ReadLineAsync(ct);if(line is null)break;size+=line.Length;Need(size<=64*1024,"Trama AMI excesiva",503);if(line.Length==0){if(fields.GetValueOrDefault("Response")=="Success")authenticated=true;if(fields.GetValueOrDefault("Response")=="Error")throw new IOException("AMI rechazó autenticación");var call=AsteriskFrames.Parse(fields);if(authenticated&&call is not null)await receive(call);fields.Clear();size=0;}else{var colon=line.IndexOf(':');if(colon>0)fields[line[..colon]]=line[(colon+1)..].Trim();}}
    }
}
public sealed partial class BranchService
{
    public async Task RecordCaller(CallerEvent call,CancellationToken ct=default)
    {
        var id=J.Hash(call.CallId+":"+call.Number)[..32];for(int attempt=0;attempt<8;attempt++){var s=await Load(ct);if(s.List("call").Any(x=>x.S("id")==id))return;var customer=s.List("customer").FirstOrDefault(x=>x.S("phone")==call.Number);s.Add("call",J.O("id",id,"siteId",siteId,"number",call.Number,"name",call.Name[..Math.Min(call.Name.Length,150)],"customerId",customer?.S("id"),"at",J.Now(),"source","asterisk-ami-tls"));try{await Commit(s,id,"configured-telephony-service",J.Hash(call.CallId),J.O("id",id),"callerid.received",ct);return;}catch(StoreException e)when(e.Code=="COS-DATA-CONFLICT"&&attempt<7){}}
    }
}
public sealed class GoogleGeocoder(HttpClient http,string apiKey)
{
    public async Task<JsonObject> Geocode(string address,CancellationToken ct=default)
    {
        Need(address.Length is >0 and <=500&&apiKey.Length>0,"Geocodificador sin configurar");using var response=await http.GetAsync("https://maps.googleapis.com/maps/api/geocode/json?address="+Uri.EscapeDataString(address)+"&key="+Uri.EscapeDataString(apiKey),ct);response.EnsureSuccessStatusCode();var data=JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;Need(data.S("status")=="OK","No se obtuvo una coordenada");var location=data.A("results").Objects().First().Obj("geometry").Obj("location");return J.O("lat",location["lat"],"lon",location["lng"],"source","google-geocoding");
    }
}
