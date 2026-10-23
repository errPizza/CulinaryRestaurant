using System.Text.Json.Nodes;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;
public sealed partial class Restaurant
{
    static readonly string[] EcosystemKinds=["call","cloudproposal","folio","printjob","customer","address","delivery","courier","zone","warehouse","ingredient","recipe","recipeversion","stock","stocklayer","stockmovement","stockjob","unitstock","transfer","ownernotice","cashadjustment","fiscal","invoicetoken","station","supplier","purchase","payable","payablepayment","stockcount","expenseaccount","expense","pettymove","pinauth","pricelist","walletmove","staffcharge","zreport","integration","integrationlog","terminaltxn","paylink","message","aggregatororder","pmspost"];
    // Largest remainder, stable index tie-break. All values are minor units; no binary float.
    public static long[] Allocate(long amount,IReadOnlyList<long> weights)
    {
        Need(amount>=0&&weights.Count>0&&weights.All(x=>x>=0),"Distribución inválida");var sum=weights.Aggregate(0m,(a,b)=>a+b);if(sum==0){Need(amount==0,"Distribución sin base");return new long[weights.Count];}
        var raw=weights.Select(x=>amount*(decimal)x/sum).ToArray();var result=raw.Select(x=>(long)decimal.Floor(x)).ToArray();var left=amount-result.Sum();foreach(var i in Enumerable.Range(0,weights.Count).OrderByDescending(i=>raw[i]-result[i]).ThenBy(i=>i).Take(checked((int)left)))result[i]++;return result;
    }
    long IncludedTax(BranchState s,JsonObject v,long total)
    {
        var units=s.Related("unit","visitId",v.S("id")).Where(x=>!new[]{"cancelled","unavailable"}.Contains(x.S("status"))).OrderBy(x=>x.S("id"),StringComparer.Ordinal).ToArray();if(units.Length==0)return 0;
        var net=Allocate(total,units.Select(x=>x.L("price")).Append(v.L("deliveryFee")).ToArray());return units.Select((x,i)=>(long)Math.Round(net[i]*(decimal)x.L("taxBps")/(10000+x.L("taxBps")),MidpointRounding.AwayFromZero)).Sum();
    }
    void GuardEcosystem(BranchState s,JsonObject u,string type,JsonObject p)
    {
        if(u.S("role")=="cajero")Need(new[]{"cash.blind.open","cash.blind.close","session.logout"}.Contains(type),"El cajero sólo captura su arqueo ciego",403);
        BackofficeGuard(s,u,type,p);
        if(type is "cash.close" or "cash.movement") {var c=s.Get("cash",p.S(type=="cash.close"?"id":"cashId"));Need(!c.B("blind"),"Usa el flujo de arqueo ciego");}
        if(type=="unit.state"&&p.S("status")=="unavailable"){var v=s.Get("visit",s.Get("unit",p.S("id")).S("visitId"));Need(!v.B("printed")&&!s.Related("folio","visitId",v.S("id")).Any(f=>!f.B("void")),"Reabre y retira subfolios antes de cambiar el consumo",409);}
        var mutations=new[]{"command.send","visit.split","visit.merge","visit.move","visit.transfer","request.create","request.decide"};
        if(mutations.Contains(type)){
            var vid=p.S("visitId",p.S("id"));if(type=="request.decide")vid=s.Get("request",p.S("id")).S("visitId");
            var v=s.Get("visit",vid);Need(!v.B("printed"),"Cuenta impresa: un gerente debe autorizar la reapertura",409);
            Need(!s.Related("folio","visitId",vid).Any(f=>!f.B("void")),"Retira la división financiera antes de modificar el consumo",409);
            if(type=="visit.merge"){var other=s.Get("visit",p.S("otherId"));Need(!other.B("printed")&&!s.Related("folio","visitId",other.S("id")).Any(f=>!f.B("void")),"Cuenta origen impresa o dividida");}
        }
    }
    void ValidatePayment(BranchState s,JsonObject v,JsonObject p,long amount)
    {
        var folios=s.Related("folio","visitId",v.S("id")).Where(x=>!x.B("void")).ToArray();
        if(folios.Length>0){var f=folios.FirstOrDefault(x=>x.S("id")==p.S("folioId"));Need(f is not null,"Selecciona un subfolio");var paid=s.Related("payment","folioId",f!.S("id")).Sum(x=>x.L("amount"));Need(paid+amount<=f!.L("total"),"El pago excede el subfolio");}else Need(p.S("folioId")=="","Subfolio inexistente");
        if(p.S("method")=="cash")Need(p.L("tendered",amount+Amount(p,"tip"))>=amount+Amount(p,"tip"),"Efectivo recibido insuficiente");
        else Need(!p.ContainsKey("tendered"),"El cambio corresponde a efectivo");
    }
    bool TryPos(BranchState s,JsonObject u,string type,JsonObject p,out JsonObject result)
    {
        result=J.O("ok",true);var id=p.S("id");
        switch(type){
        case "visit.reopen":{
            Role(u,"manager");var v=s.Get("visit",id);Need(v.S("status")=="closed","Sólo una cuenta cerrada");Need(!s.Related("fiscal","visitId",id).Any(f=>!FiscalClosed.Contains(f.S("status"))),"Resuelve la solicitud fiscal antes de reabrir");var reason=Text(p,"reason");if(v.S("tableId")!=""){var table=s.Get("table",v.S("tableId"));Need(!s.Related("visit","tableId",table.S("id")).Any(x=>x.S("status")=="open"),"La mesa ya tiene otra visita; conserva su ocupación");table["status"]="occupied";}v["status"]="open";v["printed"]=false;v["reopenedBy"]=u.S("id");v["reopenReason"]=reason;v["reopenedAt"]=J.Now();break;}
        case "visit.quick":{
            Role(u,"manager","mesero");var w=s.Get("user",p.S("waiterId",u.S("id")));Need(w.B("active")&&(w.S("role")=="mesero"||w.S("id")==u.S("id")),"Responsable inválido");Need(u.S("role")=="manager"||w.S("id")==u.S("id"),"Responsable de otra cuenta",403);
            result=Add(s,"visit",J.O("tableId","","people",1,"waiterId",w.S("id"),"status","open","channel","quick","takeout",p.B("takeout"),"customerName",Sanitize(p.S("customerName"),80),"openedAt",J.Now(),"trackingKey",Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));break;}
        case "visit.split.person":{
            var v=Own(s,u,id);var person=Amount(p,"person",1,v.L("people"));var units=s.Related("unit","visitId",id).Where(x=>x.L("person")==person).Select(x=>x.S("id")).ToArray();
            result=Act(s,u,"visit.split",J.O("id",id,"unitIds",units));break;}
        case "visit.divide":{
            Role(u,"manager","mesero");var v=Own(s,u,id);Need(!v.B("printed"),"Reabre la cuenta impresa");Need(!s.Related("payment","visitId",id).Any(),"Divide antes de registrar pagos");Need(!s.Related("folio","visitId",id).Any(x=>!x.B("void")),"Ya tiene subfolios");var n=Amount(p,"parts",2,100);var totals=Totals(s,v);var weights=Enumerable.Repeat(1L,(int)n).ToArray();var amounts=Allocate(totals.L("total"),weights);var taxes=Allocate(totals.L("tax"),amounts);var discounts=Allocate(totals.L("discount"),weights);var ids=new List<string>();
            for(int i=0;i<n;i++)ids.Add(Add(s,"folio",J.O("visitId",id,"number",i+1,"total",amounts[i],"tax",taxes[i],"discount",discounts[i],"gross",amounts[i]+discounts[i],"at",J.Now())).S("id"));result=J.O("folioIds",ids,"total",totals.L("total"));break;}
        case "visit.undivide":{var v=Own(s,u,id);Need(!v.B("printed")&&!s.Related("payment","visitId",id).Any(),"Reapertura o conciliación requerida");foreach(var f in s.Related("folio","visitId",id))f["void"]=true;break;}
        case "visit.issueBill":{
            Role(u,"manager","mesero");var v=Own(s,u,id);Need(!v.B("printed"),"Cuenta ya emitida");v["printed"]=true;v["printedAt"]=J.Now();if(v.S("tableId")!="")s.Get("table",v.S("tableId"))["status"]="printed";
            result=Add(s,"printjob",J.O("type","bill","visitId",id,"status","pending","at",J.Now(),"actor",u.S("id"),"snapshot",J.O("totals",Totals(s,v),"units",s.Related("unit","visitId",id).Select(x=>J.O("name",x.S("name"),"price",x.L("price"),"status",x.S("status"))).ToArray())));break;}
        case "visit.unlock":{
            Role(u,"manager");var v=s.Get("visit",id);Need(v.S("status")=="open"&&v.B("printed"),"Cuenta sin bloqueo de impresión");v["unlockReason"]=Text(p,"reason");v["unlockBy"]=u.S("id");v["printed"]=false;if(v.S("tableId")!="")s.Get("table",v.S("tableId"))["status"]=s.Related("visit","tableId",v.S("tableId")).Any(x=>x.S("status")=="open"&&x.B("printed"))?"printed":"occupied";break;}
        case "print.claim":{
            Role(u,"manager");var job=s.Get("printjob",id);Need(job.S("status")=="pending","Trabajo ya reclamado; no reintentar un efecto incierto",409);job["status"]="uncertain";job["attemptId"]=J.Id();job["claimedAt"]=J.Now();job["driver"]=Text(p,"driver",100);result=job;break;}
        case "print.resolve":{
            Role(u,"manager");var job=s.Get("printjob",id);Need(new[]{"uncertain","submitted"}.Contains(job.S("status"))&&job.S("attemptId")==p.S("attemptId"),"Intento físico no coincide",409);job["status"]=Choice(p,"status","confirmed","failed","uncertain");job["evidence"]=Text(p,"evidence");job["resolvedBy"]=u.S("id");job["resolvedAt"]=J.Now();break;}
        case "print.retry":{
            Role(u,"manager");var old=s.Get("printjob",id);Need(new[]{"failed","uncertain"}.Contains(old.S("status")),"Trabajo no requiere conciliación");var reason=Text(p,"reason");result=Add(s,"printjob",J.O("type",old.S("type"),"visitId",old.S("visitId"),"status","pending","snapshot",old["snapshot"],"retryOf",id,"reason",reason,"actor",u.S("id"),"at",J.Now()));break;}
        case "drawer.request":Role(u,"manager");result=Add(s,"printjob",J.O("type","drawer","status","pending","reason",Text(p,"reason"),"actor",u.S("id"),"at",J.Now()));break;
        default:return false;
        }return true;
    }
    bool TryEcosystem(BranchState s,JsonObject u,string type,JsonObject p,out JsonObject result)
    {
        var prefixes=new[]{"inventory.","stock.","recipe.","transfer.","delivery.","customer.","address.","courier.","zone.","station.","commerce.","fiscal.","print.","drawer.","cash.blind.","cash.adjust","visit.quick","visit.divide","visit.undivide","visit.split.person","visit.reopen","visit.issueBill","visit.unlock","supplier.","purchase.","payable.","expense.","petty.","cash.limit","cash.withdraw","user.pin","authorize.pin","pricelist.","visit.pricelist","visit.customer","loyalty.","wallet.","staff.","report.x","report.z","report.abc","qr.","table.qr","integration.","pac.","terminal.","paylink.","pms.","geo.","callerid.","call.","message.","aggregator."};
        if(prefixes.Any(type.StartsWith))Need(s.Meta.L("schemaVersion",1)>=2,"Requiere migración DEVMap con respaldo desde mantenimiento",503);
        return TryFront(s,u,type,p,out result)||TryBackoffice(s,u,type,p,out result)||TryLoyalty(s,u,type,p,out result)||TryClosings(s,u,type,p,out result)||TryTableQr(s,u,type,p,out result)||TryIntegrations(s,u,type,p,out result)||TryCloudProposal(s,u,type,p,out result)||TryPos(s,u,type,p,out result)||TryInventory(s,u,type,p,out result)||TryCash(s,u,type,p,out result)||TryDelivery(s,u,type,p,out result)||TryFiscal(s,u,type,p,out result);
    }
    void AfterEcosystem(BranchState s,JsonObject u,string type,JsonObject p,JsonObject result) {InventoryAfterAction(s,u,type,p,result);FrontAfterAction(s,u,type,p,result);BackofficeAfterAction(s,type,p);IntegrationAfterAction(s,u,type,p,result);}
    void FilterEcosystemSnapshot(BranchState s,JsonObject u,StateParts output)
    {
        // Only the newest 500 of a kind are returned; reach just those unless a role filter must run first.
        foreach(var kind in EcosystemKinds){var values=u.S("role")!="manager"&&kind is "cloudproposal" or "folio"?s.List(kind):s.Latest(kind,500);Func<JsonObject,JsonObject> build=x=>(JsonObject)x.DeepClone();var variant=kind;if(u.S("role")!="manager"){
            if(new[]{"cloudproposal","printjob","warehouse","ingredient","recipe","recipeversion","stock","stocklayer","stockmovement","stockjob","unitstock","transfer","ownernotice","cashadjustment","fiscal","invoicetoken","supplier","purchase","payable","payablepayment","stockcount","expenseaccount","expense","pettymove","pinauth","staffcharge","zreport"}.Contains(kind)){
                if(kind=="cloudproposal"&&u.S("role")=="mesero"){values=values.Where(x=>x.Obj("command").S("type")=="table"&&x.S("status")=="pending-review"&&x.S("visitId")!=""&&s.Get("visit",x.S("visitId")).S("waiterId")==u.S("id"));variant="cloudproposal:mesero";build=x=>J.O("id",x.S("id"),"visitId",x.S("visitId"),"tableId",x.S("tableId"),"status",x.S("status"),"command",J.O("type","table","payload",J.O("kind","order","items",x.Obj("command").Obj("payload").A("items").DeepClone(),"notes",x.Obj("command").Obj("payload").S("notes"))),"receivedAt",x.S("receivedAt"));}
                else values=[];
            }
            else if(kind=="folio")values=values.Where(x=>s.Get("visit",x.S("visitId")).S("waiterId")==u.S("id"));
            else if(u.S("role")=="cocina"&&kind!="station")values=[];
        }
        if(kind=="invoicetoken"){variant="invoicetoken:public";build=x=>J.O("id",x.S("id"),"visitId",x.S("visitId"),"expires",x.S("expires"),"used",x.B("used"));}
        output.Items(kind+"s",values.TakeLast(500).Select(x=>Project(s,variant,x,build)));}
        FilterBlindSnapshot(s,u,output);FilterIntegrationSnapshot(s,u,output);
    }
}
