using System.Text.Json.Nodes;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;
// Stage 3: X reading (no side effects), Z close (numbered and immutable) and ABC product analysis.
public sealed partial class Restaurant
{
    static bool Between(JsonObject x,string key,DateTimeOffset from,DateTimeOffset to)=>DateTimeOffset.TryParse(x.S(key),out var at)&&at>=from&&at<to;
    JsonObject ShiftSummary(BranchState s,DateTimeOffset from,DateTimeOffset to)
    {
        var payments=s.List("payment").Where(x=>Between(x,"at",from,to)).ToArray();var visits=s.List("visit").Where(x=>x.S("status")=="closed"&&Between(x,"closedAt",from,to)).ToArray();var ids=visits.Select(x=>x.S("id")).ToHashSet();
        var units=s.List("unit").Where(x=>ids.Contains(x.S("visitId"))).ToArray();var sold=units.Where(x=>!new[]{"cancelled","unavailable"}.Contains(x.S("status"))).ToArray();var voided=units.Except(sold).ToArray();
        var totals=visits.Select(v=>Totals(s,v)).ToArray();var products=s.List("product").ToDictionary(x=>x.S("id"));
        var methods=new JsonObject();foreach(var group in payments.GroupBy(x=>x.S("method")).OrderBy(g=>g.Key,StringComparer.Ordinal))methods[group.Key]=J.O("amount",group.Sum(x=>x.L("amount")),"tips",group.Sum(x=>x.L("tip")),"count",group.Count());
        var categories=new JsonArray(sold.GroupBy(x=>products.TryGetValue(x.S("productId"),out var p)?p.S("category"):"Sin categoría").OrderByDescending(g=>g.Sum(x=>x.L("price"))).Select(g=>(JsonNode)J.O("name",g.Key,"quantity",g.Count(),"amount",g.Sum(x=>x.L("price")))).ToArray());
        var waiters=new JsonArray(visits.GroupBy(x=>x.S("waiterId")).OrderByDescending(g=>g.Sum(v=>Totals(s,v).L("total"))).Select(g=>(JsonNode)J.O("waiterId",g.Key,"name",s.List("user").FirstOrDefault(x=>x.S("id")==g.Key)?.S("name")??"—","tickets",g.Count(),"amount",g.Sum(v=>Totals(s,v).L("total")))).ToArray());
        var cashes=s.List("cash").Where(c=>Between(c,"openedAt",from,to)||Between(c,"closedAt",from,to)).ToArray();var moves=s.List("movement").Where(x=>Between(x,"at",from,to)).ToArray();
        var net=totals.Sum(x=>x.L("total"));
        return J.O("from",from.ToString("O"),"to",to.ToString("O"),"tickets",visits.Length,"people",visits.Sum(x=>x.L("people")),"gross",totals.Sum(x=>x.L("gross")),"discounts",totals.Sum(x=>x.L("discount")),"net",net,"tax",totals.Sum(x=>x.L("tax")),"average",visits.Length==0?0:net/visits.Length,
            "payments",payments.Sum(x=>x.L("amount")),"tips",payments.Sum(x=>x.L("tip")),"methods",methods,"voidedCount",voided.Length,"voidedAmount",voided.Sum(x=>x.L("price")),"comps",visits.Count(v=>v.Obj("discount").S("kind")=="percent"&&v.Obj("discount")["value"]?.GetValue<decimal>()==100),
            "staffMeals",visits.Count(x=>x.S("channel")=="staff"),"walletRedeemed",payments.Where(x=>x.S("method")=="wallet").Sum(x=>x.L("amount")),"categories",categories,"waiters",waiters,
            "cash",J.O("sessions",cashes.Length,"income",moves.Where(x=>x.S("type")=="income").Sum(x=>x.L("amount")),"expense",moves.Where(x=>x.S("type")=="expense").Sum(x=>x.L("amount")),"withdrawal",moves.Where(x=>x.S("type")=="withdrawal").Sum(x=>x.L("amount")),"difference",cashes.Where(c=>c.S("status")=="closed").Sum(c=>c.L("difference"))),
            "openAccounts",s.List("visit").Count(x=>x.S("status")=="open"));
    }
    DateTimeOffset LastZ(BranchState s)=>DateTimeOffset.TryParse(s.Meta.S("lastZAt"),out var at)?at:new DateTimeOffset(MexicoNow().Date,MexicoNow().Offset);
    long PortionCost(BranchState s,JsonObject product)
    {
        if(product.S("recipeVersionId")=="")return -1;
        try{long cost=0;foreach(var line in Explode(s,product.S("recipeVersionId"),1000)){var stocks=s.List("stock").Where(x=>x.S("ingredientId")==line.Key&&x.L("quantity")>0).ToArray();var qty=stocks.Sum(x=>x.L("quantity"));var rate=qty>0?CostRate(stocks.Sum(x=>x.L("value")),qty):s.List("ingredient").FirstOrDefault(i=>i.S("id")==line.Key)?.L("lastCost")??0;cost+=CostAt(rate,line.Value);}return cost;}
        catch(BusinessException){return -1;}
    }
    bool TryClosings(BranchState s,JsonObject u,string type,JsonObject p,out JsonObject result)
    {
        result=J.O("ok",true);
        switch(type){
        case "report.x":{Role(u,"manager");var from=LastZ(s);result=ShiftSummary(s,from,DateTimeOffset.UtcNow.AddSeconds(1));result["kind"]="X";result["nextZ"]=s.Meta.L("zNumber")+1;break;}
        case "report.z":{
            Role(u,"manager");Need(!s.List("cash").Any(x=>x.S("status")=="open"),"Cierra la caja antes del corte Z");Need(!s.List("visit").Any(x=>x.S("status")=="open"),"Cierra o cancela las cuentas abiertas antes del corte Z");
            var from=LastZ(s);var to=DateTimeOffset.UtcNow;Need(to>from,"Ya existe un corte Z para este momento",409);var summary=ShiftSummary(s,from,to);s.Meta["zNumber"]=s.Meta.L("zNumber")+1;s.Meta["lastZAt"]=to.ToString("O");
            var z=Add(s,"zreport",J.O("number",s.Meta.L("zNumber"),"from",from.ToString("O"),"to",to.ToString("O"),"summary",summary,"actor",u.S("id"),"at",J.Now(),"digest",J.Hash(J.Canonical(summary))));
            Add(s,"printjob",J.O("type","zreport","zreportId",z.S("id"),"status","pending","actor",u.S("id"),"at",J.Now()));result=z;break;}
        case "report.abc":{
            Role(u,"manager");var okFrom=DateTimeOffset.TryParse(p.S("from"),out var from);var okTo=DateTimeOffset.TryParse(p.S("to"),out var to);Need(okFrom&&okTo&&from<to&&(to-from).TotalDays<=366,"Periodo inválido (máximo un año)");
            var ids=s.List("visit").Where(x=>x.S("status")=="closed"&&Between(x,"closedAt",from,to)).Select(x=>x.S("id")).ToHashSet();var products=s.List("product").ToDictionary(x=>x.S("id"));
            var rows=s.List("unit").Where(x=>ids.Contains(x.S("visitId"))&&!new[]{"cancelled","unavailable"}.Contains(x.S("status"))).GroupBy(x=>x.S("productId")).Select(g=>{var product=products.GetValueOrDefault(g.Key);var cost=product is null?-1:PortionCost(s,product);var revenue=g.Sum(x=>x.L("price"));return J.O("productId",g.Key,"name",product?.S("name")??g.First().S("name"),"category",product?.S("category")??"","quantity",g.Count(),"revenue",revenue,"unitCost",cost,"margin",cost<0?null:revenue-cost*g.Count());}).OrderByDescending(x=>x.L("revenue")).ThenBy(x=>x.S("name"),StringComparer.Ordinal).ToList();
            var total=rows.Sum(x=>x.L("revenue"));long running=0;foreach(var row in rows){var before=running;running+=row.L("revenue");row["share"]=total==0?0:Math.Round(row.L("revenue")*100m/total,2);row["class"]=total==0||before*100<total*80?"A":before*100<total*95?"B":"C";}
            var byQty=rows.OrderByDescending(x=>x.L("quantity")).ToList();var units=byQty.Sum(x=>x.L("quantity"));long seen=0;foreach(var row in byQty){row["quantityClass"]=units==0||seen*100<units*80?"A":seen*100<units*95?"B":"C";seen+=row.L("quantity");}
            result=J.O("from",from.ToString("O"),"to",to.ToString("O"),"revenue",total,"quantity",units,"rows",new JsonArray(rows.Select(x=>(JsonNode)x).ToArray()));break;}
        default:return false;
        }return true;
    }
}
