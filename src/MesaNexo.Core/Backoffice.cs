using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;
// Stage 2 back office: suppliers, purchases, payables, counts, expenses, petty cash, cash limit and PIN authorization.
public sealed partial class Restaurant
{
    static readonly string[] PinActions=["request.decide","visit.unlock","visit.reopen","drawer.request","cash.withdraw"];
    public static readonly string[] UserPermissions=["payments","shiftReports","quickSale","mergeTables","transfer"];
    const int PinAttempts=5;
    long DrawerCash(BranchState s,JsonObject cash)=>cash.L("initial")+s.List("payment").Where(x=>x.S("cashId")==cash.S("id")&&x.S("method")=="cash").Sum(x=>x.L("amount")+x.L("tip"))+s.List("movement").Where(x=>x.S("cashId")==cash.S("id")).Sum(x=>x.S("type")=="income"?x.L("amount"):-x.L("amount"));
    long PettyBalance(BranchState s)=>s.List("pettymove").Sum(x=>x.S("type")=="in"?x.L("amount"):-x.L("amount"));
    JsonObject OpenRegister(BranchState s){var cash=s.List("cash").FirstOrDefault(x=>x.S("status")=="open");Need(cash is not null,"Abre una caja para pagar desde el efectivo");Need(!cash!.B("blind"),"La caja abierta es de arqueo ciego; usa caja chica o banco");return cash;}
    static long OptionalQuantity(JsonObject p,string key,string unit)
    {
        var raw=p[key];if(raw is null||(raw is JsonValue v&&v.TryGetValue<string>(out var t)&&t.Trim()==""))return 0;
        if(raw is JsonValue z&&((z.TryGetValue<long>(out var n)&&n==0)||(z.TryGetValue<string>(out var s)&&decimal.TryParse(s,System.Globalization.NumberStyles.Number,System.Globalization.CultureInfo.InvariantCulture,out var d)&&d==0)))return 0;
        return ExactQuantity(J.O("quantity",raw.DeepClone(),"unit",unit));
    }
    // Cents per 1000 base units (per kg, per l, per 1000 pieces); estimates only, stock keeps exact layer costs.
    static long CostRate(long cost,long quantity)=>quantity<=0?0:(long)Math.Round(cost*1_000_000m/quantity,MidpointRounding.AwayFromZero);
    static long CostAt(long rate,long quantity)=>(long)Math.Round(rate*(decimal)quantity/1_000_000m,MidpointRounding.AwayFromZero);
    JsonObject? FindStock(BranchState s,string warehouse,string ingredient)=>s.List("stock").FirstOrDefault(x=>x.S("id")==warehouse+"-"+ingredient);
    JsonArray PurchaseLines(BranchState s,JsonArray lines,bool allowZero=false)
    {
        Need(lines.Count is >0 and <=30&&lines.Objects().Count()==lines.Count,"Compra: de 1 a 30 renglones");Need(lines.Objects().Select(x=>x.S("ingredientId")).Distinct().Count()==lines.Count,"Insumo repetido en la compra");var result=new JsonArray();
        foreach(var line in lines.Objects()){var i=s.Get("ingredient",line.S("ingredientId"));var unit=line.S("unit","base");ValidateIngredientUnit(i,unit);var quantity=allowZero?OptionalQuantity(line,"quantity",unit):ExactQuantity(line);result.Add(J.O("ingredientId",i.S("id"),"quantity",quantity,"cost",Amount(line,"cost")));}
        return result;
    }
    JsonObject CreateOrder(BranchState s,JsonObject u,string supplierId,string warehouseId,JsonArray lines,string notes)
    {
        var supplier=s.Get("supplier",supplierId);Need(supplier.B("active"),"Proveedor inactivo");s.Get("warehouse",warehouseId);s.Meta["purchaseNumber"]=s.Meta.L("purchaseNumber")+1;
        return Add(s,"purchase",J.O("number",s.Meta.L("purchaseNumber"),"supplierId",supplierId,"warehouseId",warehouseId,"status","ordered","lines",lines,"total",lines.Objects().Sum(x=>x.L("cost")),"notes",Sanitize(notes,500),"orderedBy",u.S("id"),"at",J.Now()));
    }
    JsonObject PayPayable(BranchState s,JsonObject u,JsonObject payable,JsonObject p)
    {
        Need(payable.S("status")=="open","La cuenta por pagar ya está liquidada",409);var balance=payable.L("amount")-payable.L("paid");var amount=Amount(p,"amount",1,balance);var method=Choice(p,"method","transfer","cash","check","card");var source=Choice(p,"source","bank","register","petty");
        Need(source=="bank"||method=="cash","Caja y caja chica sólo pagan en efectivo");var supplier=s.Get("supplier",payable.S("supplierId"));var reason="Pago a "+supplier.S("name")+" · "+payable.S("invoice");
        var pay=Add(s,"payablepayment",J.O("payableId",payable.S("id"),"supplierId",supplier.S("id"),"amount",amount,"method",method,"source",source,"reference",Sanitize(p.S("reference"),100),"actor",u.S("id"),"at",J.Now()));
        if(source=="register"){var cash=OpenRegister(s);Need(DrawerCash(s,cash)>=amount,"No hay suficiente efectivo en caja");Add(s,"movement",J.O("cashId",cash.S("id"),"type","expense","amount",amount,"reason",reason,"payableId",payable.S("id"),"actor",u.S("id"),"at",J.Now()));}
        if(source=="petty"){Need(PettyBalance(s)>=amount,"Saldo insuficiente en caja chica");Add(s,"pettymove",J.O("type","out","amount",amount,"reason",reason,"payableId",payable.S("id"),"actor",u.S("id"),"at",J.Now()));}
        payable["paid"]=payable.L("paid")+amount;if(payable.L("paid")==payable.L("amount")){payable["status"]="paid";payable["paidAt"]=J.Now();}return pay;
    }
    // Argon2id needs at least 12 characters; binding the site and user also keeps two equal PINs from sharing a hash.
    static string PinMaterial(BranchState s,JsonObject user,string code)=>"mesanexo-pin|"+s.Meta.S("siteId")+"|"+user.S("id")+"|"+code;
    static void ValidatePasscode(string code)
    {
        Need(Regex.IsMatch(code,"^[0-9]{4,6}$"),"El PIN debe tener de 4 a 6 dígitos");Need(code.Distinct().Count()>1,"El PIN no puede repetir un solo dígito");
        Need(!"01234567890".Contains(code)&&!"09876543210".Contains(code),"El PIN no puede ser una secuencia");
    }
    void BackofficeGuard(BranchState s,JsonObject u,string type,JsonObject p)
    {
        if(u.S("role")!="manager"){
            var perms=u.Obj("permissions");
            if(type=="sale.quick")Need(perms.B("quickSale",true),"Permiso de venta en mostrador deshabilitado",403);
            if(type is "visit.merge" or "visit.mergeMany")Need(perms.B("mergeTables",true),"Permiso para unir mesas deshabilitado",403);
            if(type is "visit.transfer" or "visit.move")Need(perms.B("transfer",true),"Permiso para transferir cuentas deshabilitado",403);
        }
        if(type=="payment.add"&&p.S("method")=="cash"&&s.Meta.L("cashLimit")>0&&s.Meta.B("cashLimitBlock")){
            var cash=s.List("cash").FirstOrDefault(x=>x.S("status")=="open");if(cash is not null&&!cash.B("blind"))Need(DrawerCash(s,cash)<s.Meta.L("cashLimit"),"El efectivo en caja superó el límite; un gerente debe hacer un retiro parcial antes de seguir cobrando en efectivo",409);
        }
    }
    void BackofficeAfterAction(BranchState s,string type,JsonObject p)
    {
        if(type!="payment.add"||p.S("method")!="cash"||s.Meta.L("cashLimit")<=0)return;var cash=s.List("cash").FirstOrDefault(x=>x.S("status")=="open");if(cash is null||cash.B("blind"))return;
        var drawer=DrawerCash(s,cash);if(drawer>=s.Meta.L("cashLimit")&&!cash.B("limitAlerted")){cash["limitAlerted"]=true;Add(s,"ownernotice",J.O("type","cash-limit","cashId",cash.S("id"),"amount",drawer,"limit",s.Meta.L("cashLimit"),"status","pending","at",J.Now()));}
    }
    bool TryBackoffice(BranchState s,JsonObject u,string type,JsonObject p,out JsonObject result)
    {
        result=J.O("ok",true);var id=p.S("id");
        switch(type){
        case "user.pin":{
            var user=s.Get("user",id==""?u.S("id"):id);Need(u.S("role")=="manager"||user.S("id")==u.S("id"),"Sólo un gerente asigna el PIN de otra persona",403);
            if(p.B("clear")){Role(u,"manager");user.Remove("pinHash");user["pinFails"]=0;user.Remove("pinLockedUntil");result=J.O("id",user.S("id"),"hasPin",false);break;}
            var code=p.S("passcode");ValidatePasscode(code);user["pinHash"]=passwords.Hash(PinMaterial(s,user,code));user["pinSetAt"]=J.Now();user["pinFails"]=0;user.Remove("pinLockedUntil");result=J.O("id",user.S("id"),"hasPin",true);break;}
        case "authorize.pin":{
            var approver=s.Get("user",p.S("approverId"));Need(approver.B("active")&&approver.S("role")=="manager","Elige un gerente activo",403);Need(approver.S("pinHash")!="","Ese gerente no tiene PIN asignado",403);
            var action=p.Obj("action");var actionType=action.S("type");Need(PinActions.Contains(actionType),"Esta operación no admite autorización con PIN",403);
            Need(!(DateTimeOffset.TryParse(approver.S("pinLockedUntil"),out var locked)&&locked>DateTimeOffset.UtcNow),"PIN bloqueado por intentos fallidos; espera 5 minutos",423);
            var payload=action.Obj("payload");
            if(!Regex.IsMatch(p.S("passcode"),"^[0-9]{4,6}$")||!passwords.Verify(approver.S("pinHash"),PinMaterial(s,approver,p.S("passcode")))){
                var fails=approver.L("pinFails")+1;if(fails>=PinAttempts){approver["pinLockedUntil"]=DateTimeOffset.UtcNow.AddMinutes(5).ToString("O");fails=0;}approver["pinFails"]=fails;
                Add(s,"pinauth",J.O("actor",u.S("id"),"approverId",approver.S("id"),"actionType",actionType,"targetId",payload.S("id"),"ok",false,"at",J.Now()));
                result=J.O("ok",false,"error",fails==0?"PIN incorrecto. Se bloqueó por 5 minutos":"PIN incorrecto","remaining",fails==0?0:PinAttempts-fails);break;}
            approver["pinFails"]=0;var inner=Act(s,approver,actionType,(JsonObject)payload.DeepClone());
            Add(s,"pinauth",J.O("actor",u.S("id"),"approverId",approver.S("id"),"actionType",actionType,"targetId",payload.S("id"),"ok",true,"at",J.Now()));
            result=J.O("ok",true,"authorizedBy",approver.S("id"),"result",inner);break;}
        case "supplier.save":{
            Role(u,"manager");var name=Text(p,"name",150);Need(!s.List("supplier").Any(x=>x.S("name").Equals(name,StringComparison.OrdinalIgnoreCase)&&x.S("id")!=id),"Proveedor repetido");
            var rfc=p.S("rfc").Trim().ToUpperInvariant();Need(rfc==""||Regex.IsMatch(rfc,"^[A-ZÑ&]{3,4}[0-9]{6}[A-Z0-9]{3}$"),"RFC inválido");var email=p.S("email").Trim();Need(email==""||(email.Length<=150&&Regex.IsMatch(email,"^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$")),"Correo inválido");
            var supplier=id==""?Add(s,"supplier",J.O("at",J.Now())):s.Get("supplier",id);supplier["name"]=name;supplier["rfc"]=rfc;supplier["email"]=email;supplier["contact"]=Sanitize(p.S("contact"),150);supplier["phone"]=Sanitize(p.S("phone"),40);supplier["creditDays"]=Amount(p,"creditDays",0,180);supplier["active"]=p.B("active",true);result=supplier;break;}
        case "inventory.levels":{
            Role(u,"manager");var i=s.Get("ingredient",p.S("ingredientId"));var unit=p.S("unit","base");ValidateIngredientUnit(i,unit);var min=OptionalQuantity(p,"min",unit);var max=OptionalQuantity(p,"max",unit);Need(max==0||max>=min,"El máximo debe ser mayor o igual al mínimo");
            if(p.S("supplierId")!="")Need(s.Get("supplier",p.S("supplierId")).B("active"),"Proveedor inactivo");i["minStock"]=min;i["maxStock"]=max;i["supplierId"]=p.S("supplierId");result=i;break;}
        case "purchase.order":Role(u,"manager");result=CreateOrder(s,u,p.S("supplierId"),p.S("warehouseId"),PurchaseLines(s,p.A("lines")),p.S("notes"));break;
        case "purchase.cancel":{Role(u,"manager");var order=s.Get("purchase",id);Need(order.S("status")=="ordered","Sólo se cancela una orden sin recibir",409);order["status"]="cancelled";order["cancelReason"]=Text(p,"reason",300);order["cancelledBy"]=u.S("id");order["cancelledAt"]=J.Now();break;}
        case "purchase.receive":{
            Role(u,"manager");var order=id==""?CreateOrder(s,u,p.S("supplierId"),p.S("warehouseId"),PurchaseLines(s,p.A("lines")),p.S("notes")):s.Get("purchase",id);Need(order.S("status")=="ordered","La orden ya fue recibida o cancelada",409);
            var lines=id!=""&&p.A("lines").Count>0?PurchaseLines(s,p.A("lines"),true):order.A("lines");var invoice=Text(p,"invoice",60);var supplier=s.Get("supplier",order.S("supplierId"));
            foreach(var line in lines.Objects().Where(x=>x.L("quantity")>0)){ReceiveStock(s,order.S("warehouseId"),line.S("ingredientId"),line.L("quantity"),line.L("cost"),"purchase",order.S("id"),u.S("id"));s.Get("ingredient",line.S("ingredientId"))["lastCost"]=CostRate(line.L("cost"),line.L("quantity"));}
            var total=lines.Objects().Sum(x=>x.L("cost"));Need(lines.Objects().Any(x=>x.L("quantity")>0),"Recibe al menos un insumo");
            var ordered=order.A("lines").Objects().ToDictionary(x=>x.S("ingredientId"),x=>x.L("quantity"));var differences=lines.Objects().Count(x=>ordered.GetValueOrDefault(x.S("ingredientId"))!=x.L("quantity"))+ordered.Keys.Count(k=>!lines.Objects().Any(x=>x.S("ingredientId")==k));
            order["status"]="received";order["received"]=lines.DeepClone();order["receivedTotal"]=total;order["invoice"]=invoice;order["differences"]=differences;order["receivedBy"]=u.S("id");order["receivedAt"]=J.Now();
            JsonObject? payable=null;
            if(total>0){var days=p.ContainsKey("dueDays")?Amount(p,"dueDays",0,180):supplier.L("creditDays");payable=Add(s,"payable",J.O("supplierId",supplier.S("id"),"purchaseId",order.S("id"),"invoice",invoice,"amount",total,"paid",0,"status","open","dueAt",DateTimeOffset.UtcNow.AddDays(days).ToString("O"),"at",J.Now()));
                if(p.B("payNow"))PayPayable(s,u,payable,J.O("amount",total,"method",p.S("method","cash"),"source",p.S("source","register"),"reference",invoice));}
            result=J.O("id",order.S("id"),"total",total,"differences",differences,"payableId",payable?.S("id"));break;}
        case "purchase.suggest":{
            Role(u,"manager");var warehouse=s.Get("warehouse",p.S("warehouseId")).S("id");var lines=new List<JsonObject>();
            foreach(var i in s.List("ingredient").Where(x=>x.L("maxStock")>0)){
                var stock=FindStock(s,warehouse,i.S("id"));var available=(stock?.L("quantity")??0)-(stock?.L("reserved")??0);
                var incoming=s.List("purchase").Where(x=>x.S("status")=="ordered"&&x.S("warehouseId")==warehouse).SelectMany(x=>x.A("lines").Objects()).Where(x=>x.S("ingredientId")==i.S("id")).Sum(x=>x.L("quantity"));
                if(available+incoming>i.L("minStock"))continue;var suggest=i.L("maxStock")-available-incoming;if(suggest<=0)continue;
                lines.Add(J.O("ingredientId",i.S("id"),"name",i.S("name"),"unit",i.S("unit"),"available",available,"incoming",incoming,"min",i.L("minStock"),"max",i.L("maxStock"),"suggest",suggest,"supplierId",i.S("supplierId"),"estimated",CostAt(i.L("lastCost"),suggest)));
            }
            var orders=new JsonArray();
            if(p.B("create"))foreach(var group in lines.Where(x=>x.S("supplierId")!=""&&s.Get("supplier",x.S("supplierId")).B("active")).GroupBy(x=>x.S("supplierId")))
                foreach(var chunk in group.Chunk(30))orders.Add(CreateOrder(s,u,group.Key,warehouse,new JsonArray(chunk.Select(x=>(JsonNode)J.O("ingredientId",x.S("ingredientId"),"quantity",x.L("suggest"),"cost",x.L("estimated"))).ToArray()),"Sugerida por mínimos y máximos").S("id"));
            result=J.O("warehouseId",warehouse,"lines",new JsonArray(lines.Select(x=>(JsonNode)x).ToArray()),"orders",orders,"withoutSupplier",lines.Count(x=>x.S("supplierId")==""));break;}
        case "payable.pay":Role(u,"manager");result=PayPayable(s,u,s.Get("payable",id),p);break;
        case "stock.count":{
            Role(u,"manager");var warehouse=s.Get("warehouse",p.S("warehouseId")).S("id");var input=p.A("lines");Need(input.Count is >0 and <=25&&input.Objects().Count()==input.Count,"Conteo: de 1 a 25 insumos por captura");Need(input.Objects().Select(x=>x.S("ingredientId")).Distinct().Count()==input.Count,"Insumo repetido en el conteo");
            var count=Add(s,"stockcount",J.O("warehouseId",warehouse,"note",Sanitize(p.S("note"),300),"actor",u.S("id"),"at",J.Now()));var lines=new JsonArray();long shortage=0,surplus=0;
            foreach(var line in input.Objects()){
                var i=s.Get("ingredient",line.S("ingredientId"));var unit=line.S("unit","base");ValidateIngredientUnit(i,unit);var counted=OptionalQuantity(line,"quantity",unit);var stock=FindStock(s,warehouse,i.S("id"));var theoretical=stock?.L("quantity")??0;var difference=counted-theoretical;long cost=0;
                if(difference<0){Need(theoretical-(stock?.L("reserved")??0)>=-difference,"El conteo de "+i.S("name")+" es menor que lo reservado por comandas abiertas; liquida o cancela antes de contar",409);cost=Consume(s,warehouse,i.S("id"),-difference,"count-shortage",count.S("id"),u.S("id")).L("cost");shortage-=cost;}
                if(difference>0){var rate=stock is not null&&stock.L("quantity")>0?CostRate(stock.L("value"),stock.L("quantity")):i.L("lastCost");cost=CostAt(rate,difference);ReceiveStock(s,warehouse,i.S("id"),difference,cost,"count-surplus",count.S("id"),u.S("id"));surplus+=cost;}
                lines.Add(J.O("ingredientId",i.S("id"),"theoretical",theoretical,"counted",counted,"difference",difference,"cost",cost));
            }
            count["lines"]=lines;count["shortageCost"]=shortage;count["surplusCost"]=surplus;result=count;break;}
        case "inventory.variance":{
            Role(u,"manager");var okFrom=DateTimeOffset.TryParse(p.S("from"),out var from);var okTo=DateTimeOffset.TryParse(p.S("to"),out var to);Need(okFrom&&okTo&&from<to&&(to-from).TotalDays<=366,"Periodo inválido (máximo un año)");var warehouse=p.S("warehouseId");
            bool In(JsonObject x)=>DateTimeOffset.TryParse(x.S("at"),out var at)&&at>=from&&at<to&&(warehouse==""||x.S("warehouseId")==warehouse);
            var moves=s.List("stockmovement").Where(In).ToArray();var rows=new JsonArray();long theo=0,waste=0,shortage=0,surplus=0;
            foreach(var group in moves.GroupBy(x=>x.S("ingredientId"))){
                long Sum(params string[] kinds)=>-group.Where(x=>kinds.Contains(x.S("type"))).Sum(x=>x.L("cost"));long Qty(params string[] kinds)=>-group.Where(x=>kinds.Contains(x.S("type"))).Sum(x=>x.L("quantity"));
                var t=Sum("sale","prepared-waste");var w=Sum("waste");var sh=Sum("count-shortage");var su=-Sum("count-surplus");if(t==0&&w==0&&sh==0&&su==0)continue;
                theo+=t;waste+=w;shortage+=sh;surplus+=su;var ingredient=s.List("ingredient").FirstOrDefault(x=>x.S("id")==group.Key);
                rows.Add(J.O("ingredientId",group.Key,"name",ingredient?.S("name")??group.Key,"unit",ingredient?.S("unit")??"g","theoreticalQty",Qty("sale","prepared-waste"),"realQty",Qty("sale","prepared-waste","waste","count-shortage")+Qty("count-surplus"),"theoretical",t,"waste",w,"shortage",sh,"surplus",su,"real",t+w+sh-su));
            }
            var sales=s.List("payment").Where(x=>DateTimeOffset.TryParse(x.S("at"),out var at)&&at>=from&&at<to).Sum(x=>x.L("amount"));
            result=J.O("from",from.ToString("O"),"to",to.ToString("O"),"sales",sales,"theoretical",theo,"waste",waste,"shortage",shortage,"surplus",surplus,"real",theo+waste+shortage-surplus,"rows",rows);break;}
        case "expense.account":{
            Role(u,"manager");var name=Text(p,"name",100);Need(!s.List("expenseaccount").Any(x=>x.S("name").Equals(name,StringComparison.OrdinalIgnoreCase)&&x.S("id")!=id),"Cuenta de gasto repetida");
            var account=id==""?Add(s,"expenseaccount",J.O("at",J.Now())):s.Get("expenseaccount",id);account["name"]=name;account["budget"]=Amount(p,"budget");account["active"]=p.B("active",true);result=account;break;}
        case "expense.record":{
            Role(u,"manager");var account=s.Get("expenseaccount",p.S("accountId"));Need(account.B("active"),"Cuenta de gasto inactiva");var amount=Amount(p,"amount",1);var source=Choice(p,"source","petty","register","bank");var description=Text(p,"description",200);
            if(p.S("supplierId")!="")s.Get("supplier",p.S("supplierId"));var expense=Add(s,"expense",J.O("accountId",account.S("id"),"amount",amount,"source",source,"description",Sanitize(description,200),"supplierId",p.S("supplierId"),"receipt",Sanitize(p.S("receipt"),60),"actor",u.S("id"),"at",J.Now()));var reason="Gasto · "+account.S("name")+" · "+description;
            if(source=="register"){var cash=OpenRegister(s);Need(DrawerCash(s,cash)>=amount,"No hay suficiente efectivo en caja");Add(s,"movement",J.O("cashId",cash.S("id"),"type","expense","amount",amount,"reason",reason,"expenseId",expense.S("id"),"accountId",account.S("id"),"actor",u.S("id"),"at",J.Now()));}
            if(source=="petty"){Need(PettyBalance(s)>=amount,"Saldo insuficiente en caja chica; repónla primero");Add(s,"pettymove",J.O("type","out","amount",amount,"reason",reason,"expenseId",expense.S("id"),"actor",u.S("id"),"at",J.Now()));}
            result=expense;break;}
        case "petty.configure":Role(u,"manager");s.Meta["pettyFund"]=Amount(p,"fund");break;
        case "petty.fund":{
            Role(u,"manager");var amount=Amount(p,"amount",1);var fromRegister=p.B("fromRegister");var reason=Text(p,"reason",200);
            if(fromRegister){var cash=OpenRegister(s);Need(DrawerCash(s,cash)>=amount,"No hay suficiente efectivo en caja");Add(s,"movement",J.O("cashId",cash.S("id"),"type","withdrawal","amount",amount,"reason","Reposición de caja chica · "+reason,"actor",u.S("id"),"at",J.Now()));}
            result=Add(s,"pettymove",J.O("type","in","amount",amount,"reason",reason,"fromRegister",fromRegister,"actor",u.S("id"),"at",J.Now()));break;}
        case "cash.limit":Role(u,"manager");s.Meta["cashLimit"]=Amount(p,"limit");s.Meta["cashLimitBlock"]=p.B("block");break;
        case "cash.withdraw":{
            Role(u,"manager");var cash=OpenRegister(s);var drawer=DrawerCash(s,cash);var amount=Amount(p,"amount",1,Math.Max(1,drawer));Need(amount<=drawer,"El retiro excede el efectivo en caja");
            var movement=Add(s,"movement",J.O("cashId",cash.S("id"),"type","withdrawal","partial",true,"amount",amount,"reason",p.S("reason")==""?"Retiro parcial":Text(p,"reason",200),"envelope",Sanitize(p.S("envelope"),40),"actor",u.S("id"),"at",J.Now()));
            if(drawer-amount<s.Meta.L("cashLimit"))cash["limitAlerted"]=false;result=J.O("id",movement.S("id"),"drawer",drawer-amount);break;}
        default:return false;
        }return true;
    }
}
