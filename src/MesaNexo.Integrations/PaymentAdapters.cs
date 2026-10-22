using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Integrations;
public static class SignedWebhooks
{
    public static JsonObject Stripe(byte[] body,string signature,string secret,DateTimeOffset? now=null)
    {
        Need(body.Length<=112*1024&&secret.Length>=20,"Webhook inválido",403);var parts=signature.Split(',').Select(x=>x.Split('=',2)).Where(x=>x.Length==2).ToArray();var timestamp=parts.FirstOrDefault(x=>x[0]=="t")?.Last();Need(long.TryParse(timestamp,out var seconds)&&Math.Abs((now??DateTimeOffset.UtcNow).ToUnixTimeSeconds()-seconds)<=300,"Firma fuera de vigencia",403);var raw=Encoding.UTF8.GetBytes(timestamp+".").Concat(body).ToArray();var expected=HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),raw);bool match=false;foreach(var sig in parts.Where(x=>x[0]=="v1")){try{match|=CryptographicOperations.FixedTimeEquals(expected,Convert.FromHexString(sig[1]));}catch(FormatException){}}
        Need(match,"Firma de webhook inválida",403);return JsonNode.Parse(body)!.AsObject();
    }
    public static JsonObject DeliveryV1(byte[] body,string timestamp,string signature,string secret,DateTimeOffset? now=null)
    {
        Need(body.Length<=112*1024&&secret.Length>=32,"Webhook inválido",403);Need(long.TryParse(timestamp,out var seconds)&&Math.Abs((now??DateTimeOffset.UtcNow).ToUnixTimeSeconds()-seconds)<=300,"Webhook vencido",403);var expected=HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),Encoding.UTF8.GetBytes(timestamp+".").Concat(body).ToArray());byte[] supplied;try{supplied=Convert.FromHexString(signature);}catch{throw new BusinessException(403,"Firma inválida");}Need(CryptographicOperations.FixedTimeEquals(expected,supplied),"Firma inválida",403);return JsonNode.Parse(body)!.AsObject();
    }
}
public sealed class StripeSandboxPayments(HttpClient http,string secretKey)
{
    public async Task<JsonObject> CreateIntent(string operationId,long amount,string tenant,string branch,string order,CancellationToken ct=default)
    {
        Need(secretKey.StartsWith("sk_test_")&&amount>0&&Guid.TryParse(operationId,out _),"Sólo Stripe test con UUID e importe válidos");using var req=new HttpRequestMessage(HttpMethod.Post,"https://api.stripe.com/v1/payment_intents");req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",secretKey);req.Headers.Add("Idempotency-Key",operationId);req.Content=new FormUrlEncodedContent(new Dictionary<string,string>{{"amount",amount.ToString(System.Globalization.CultureInfo.InvariantCulture)},{"currency","mxn"},{"automatic_payment_methods[enabled]","true"},{"metadata[tenant_id]",tenant},{"metadata[branch_id]",branch},{"metadata[order_id]",order}});using var response=await http.SendAsync(req,ct);response.EnsureSuccessStatusCode();return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
    }
}
