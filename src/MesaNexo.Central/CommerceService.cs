using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Central;
// A branch's Deliverect location: the HMAC secret of its webhooks and Deliverect's channel ids for uber, rappi and didi.
public sealed record DeliverectLocation(string Secret,Dictionary<string,string> Channels)
{
    // deliverect.json: {"<slug>": {"secret": "…", "channels": {"7": "uber", …}}}
    public static Dictionary<string,DeliverectLocation> Load(string path)
    {
        var o=JsonNode.Parse(File.ReadAllText(path)) as JsonObject??throw new BusinessException(400,"Configuración de Deliverect inválida");
        return o.Where(x=>x.Value is JsonObject).ToDictionary(x=>x.Key,x=>{var v=(JsonObject)x.Value!;return new DeliverectLocation(Text(v,"secret",300),v.Obj("channels").Where(c=>c.Value is not null&&new[]{"uber","rappi","didi"}.Contains(c.Value.ToString())).ToDictionary(c=>c.Key,c=>c.Value!.ToString()));});
    }
}
// Business service only: no listener, deployment, credentials or external calls.
public sealed class CommerceService(CentralStore store)
{
    public JsonObject Catalog(string slug)
    {
        var scope=store.PublicScope(slug);var products=store.Mirror(scope,"product").Where(x=>x.B("visible")&&x.B("available")).Select(x=>J.O("id",x.S("id"),"name",x.S("name"),"description",x.S("description"),"category",x.S("category"),"price",x.L("price"),"image",x.S("image"),"supportsCooking",x.B("supportsCooking"),"extras",x.A("extras"))).ToArray();
        var meta=store.Mirror(scope,"meta").FirstOrDefault();return J.O("name",meta?.S("name")??"Culinary Restaurant","products",products,"source","branch-event-mirror","updatedAt",meta?.S("sourceAt"),"receivedAt",meta?.S("receivedAt"),"onlinePaymentConfigured",false);
    }
    public JsonObject Checkout(string slug,JsonObject request)
    {
        var scope=store.PublicScope(slug);var (lines,gross)=Quote(scope,request.A("items"));
        var mode=Choice(request,"mode","pickup","delivery");long fee=0;if(mode=="delivery"){
                        var address=request.Obj("address");Text(address,"text",500);var lat=Restaurant.Coordinate(address,"lat",90);var lon=Restaurant.Coordinate(address,"lon",180);var zone=store.Mirror(scope,"zone").Where(z=>z.B("active")&&Restaurant.InPolygon(z.A("polygon"),lat,lon)).OrderBy(z=>z.L("priority")).ThenBy(z=>z.S("id"),StringComparer.Ordinal).FirstOrDefault();Need(zone is not null,"Entrega fuera de cobertura publicada");fee=zone!.L("fee");}
        long discount=0;var coupon=request.S("coupon").Trim().ToUpperInvariant();if(coupon!=""){
            var promo=store.Mirror(scope,"promotion").FirstOrDefault(x=>x.S("coupon")==coupon&&x.B("active"));Need(promo is not null,"Cupón no disponible");var hour=TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City")).Hour;Need(hour>=promo!.L("startHour")&&hour<promo.L("endHour")&&lines.Objects().All(x=>promo.A("productIds").Strings().Contains(x.S("productId"))),"Cupón fuera de horario o productos no aplicables");discount=(long)Math.Round(gross*promo!.L("percent")/100m,MidpointRounding.AwayFromZero);}
        var payload=J.O("requestId",request.S("requestId"),"clientToken",request.S("clientToken"),"items",lines,"name",Text(request,"name",150),"phone",Text(request,"phone",40),"mode",mode,"address",request["address"],"coupon",coupon,"discount",discount,"deliveryFee",fee,"quotedTotal",checked(gross+fee-discount),"paymentMethod",Choice(request,"paymentMethod","cash","online"));return store.StoreOrder(scope,payload);
    }
    // Prices come only from the branch projection; products that need a required choice are ordered with staff.
    (JsonArray lines,long gross) Quote(TenantScope scope,JsonArray items)
    {
        Need(items.Count is >0 and <=30&&items.Objects().Count()==items.Count,"Carrito de 1 a 30 renglones");Need(items.Objects().Sum(x=>Amount(x,"quantity",1,50))<=100,"Máximo 100 unidades");var products=store.Mirror(scope,"product");long gross=0;var lines=new JsonArray();
        foreach(var row in items.Objects()){
            var product=products.FirstOrDefault(x=>x.S("id")==row.S("productId")&&x.B("available")&&x.B("visible"));Need(product is not null,"Producto fuera del catálogo publicado",409);Need(!product!.A("modifiers").Objects().Any(g=>g.B("required"))&&!product.B("cookingRequired"),"Este producto necesita elegir opciones con el personal",409);var extras=row.A("extras").Strings();Need(extras.Distinct().Count()==extras.Length&&extras.Length<=20,"Extras inválidos");var price=product!.L("price");foreach(var extra in extras){var e=product.A("extras").Objects().FirstOrDefault(x=>x.S("name")==extra);Need(e is not null,"Extra no disponible");price=checked(price+e!.L("price"));}var qty=Amount(row,"quantity",1,50);gross=checked(gross+price*qty);lines.Add(J.O("productId",product.S("id"),"quantity",qty,"extras",extras,"person",1,"cooking",row.S("cooking"),"notes",row.S("notes")[..Math.Min(1000,row.S("notes").Length)],"quotedPrice",price));}
        return (lines,gross);
    }
    public JsonObject Invoice(string token,JsonObject receiver)=>store.SubmitInvoice(token,receiver);
    // A Deliverect order (or its cancellation, status 100) for a branch, checked against the location's shared secret and
    // queued as an "aggregator" proposal; the branch matches the items by PLU. A repeated webhook is the same request.
    public JsonObject Deliverect(string slug,byte[] raw,string signature,DeliverectLocation location)
    {
        var o=MesaNexo.Integrations.DeliverectSignature.Verify(raw,signature,location.Secret);var scope=store.PublicScope(slug);
        var id=Text(o,"_id",80);var cancel=o.L("status")==100;Need(o.L("decimalDigits",2)==2,"Importes con decimales distintos de 2");
        var hash=J.Hash("deliverect:"+id+(cancel?":cancel":""));var requestId=Guid.ParseExact(hash[..32],"N").ToString();
        var clientToken=Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(location.Secret),System.Text.Encoding.UTF8.GetBytes(requestId)));
        static JsonObject Line(JsonObject x)=>J.O("plu",x.S("plu"),"name",x.S("name"),"price",x.L("price"),"quantity",x.L("quantity",1));
        var items=new JsonArray(o.A("items").Objects().Take(50).Select(x=>{var line=Line(x);line["subItems"]=new JsonArray(x.A("subItems").Objects().Take(20).Select(y=>(JsonNode)Line(y)).ToArray());return (JsonNode)line;}).ToArray());
        var channel=o.L("channel").ToString(System.Globalization.CultureInfo.InvariantCulture);
        var payload=J.O("requestId",requestId,"clientToken",clientToken,"deliverectId",id,"cancel",cancel,"platform",location.Channels.GetValueOrDefault(channel,""),"channelName",o.S("channelName"),"displayId",o.S("channelOrderDisplayId"),"customerName",o.Obj("customer").S("name"),"notes",o.S("note"),"items",items,"total",o.Obj("payment").L("amount"),"pickupTime",o.S("pickupTime"));
        return store.StoreOrder(scope,payload,"aggregator");
    }
    JsonObject Table(TenantScope scope,string token)
    {
        Need(System.Text.RegularExpressions.Regex.IsMatch(token,"^[0-9a-f]{32}$"),"Código de mesa inválido",404);var table=store.Mirror(scope,"table").FirstOrDefault(x=>x.S("qrHash")==J.Hash(token));Need(table is not null,"Este código de mesa ya no es válido; pide uno nuevo al personal",404);return table!;
    }
    public JsonObject TableInfo(string slug,string token)
    {
        var scope=store.PublicScope(slug);var table=Table(scope,token);var catalog=Catalog(slug);catalog["products"]=new JsonArray(catalog.A("products").Objects().Where(p=>{var source=store.Mirror(scope,"product").FirstOrDefault(x=>x.S("id")==p.S("id"));return source is not null&&!source.A("modifiers").Objects().Any(g=>g.B("required"))&&!source.B("cookingRequired");}).Select(x=>(JsonNode)x.DeepClone()).ToArray());catalog["table"]=J.O("name",table.S("name"),"open",new[]{"occupied","printed"}.Contains(table.S("status")));return catalog;
    }
    public JsonObject TableRequest(string slug,string token,JsonObject request)
    {
        var scope=store.PublicScope(slug);var table=Table(scope,token);var kind=Choice(request,"kind","waiter","bill","order");var lines=new JsonArray();long total=0;if(kind=="order")(lines,total)=Quote(scope,request.A("items"));
        var notes=request.S("notes").Trim();Need(notes.Length<=300,"Notas de hasta 300 caracteres");
        return store.StoreOrder(scope,J.O("requestId",request.S("requestId"),"clientToken",request.S("clientToken"),"tableId",table.S("id"),"qrHash",table.S("qrHash"),"kind",kind,"items",lines,"quotedTotal",total,"notes",notes),"table");
    }
    public JsonObject Status(string slug,string id,string token)=>store.OrderStatus(store.PublicScope(slug),id,token);
    public JsonObject Analytics(string token,string branch)
    {
        var scope=store.AuthorizeTenant(token,branch);var meta=store.Mirror(scope,"meta").FirstOrDefault();var visits=store.Mirror(scope,"visit");var units=store.Mirror(scope,"unit");var payments=store.Mirror(scope,"payment");var closed=visits.Where(v=>v.S("status")=="closed").ToArray();var ids=closed.Select(x=>x.S("id")).ToHashSet();
        var gross=units.Where(x=>ids.Contains(x.S("visitId"))&&!new[]{"cancelled","unavailable"}.Contains(x.S("status"))).Sum(x=>x.L("price"))+closed.Sum(x=>x.L("deliveryFee"));var discounts=closed.Sum(v=>{var g=units.Where(x=>x.S("visitId")==v.S("id")&&!new[]{"cancelled","unavailable"}.Contains(x.S("status"))).Sum(x=>x.L("price"))+v.L("deliveryFee");var d=v.Obj("discount");return d.S("kind")=="percent"?(long)Math.Round(g*(d["value"]?.GetValue<decimal>()??0)/100m,MidpointRounding.AwayFromZero):Math.Min(g,d.L("value"));});
        var source=meta?.S("sourceAt");var lag=DateTimeOffset.TryParse(source,out var at)?Math.Max(0,(long)(DateTimeOffset.UtcNow-at).TotalSeconds):(long?)null;
        return J.O("tenantId",scope.TenantId,"branchId",scope.BranchId,"name",meta?.S("name"),"source","branch-event-mirror","sourceAt",source,"receivedAt",meta?.S("receivedAt"),"observedAt",J.Now(),"lagSeconds",lag,"sales",gross-discounts,"paid",payments.Sum(x=>x.L("amount")),"openTables",store.Mirror(scope,"table").Count(x=>new[]{"occupied","printed"}.Contains(x.S("status"))),"openAccounts",visits.Count(x=>x.S("status")=="open"),"incidents",store.Mirror(scope,"request").Where(x=>x.S("status")=="pending").ToArray(),"cashCloses",store.Mirror(scope,"cash").Where(x=>x.S("status")=="closed").TakeLast(100).ToArray());
    }
}
