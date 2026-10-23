using System.Text.Json.Nodes;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;
public sealed partial class Restaurant
{
    bool TryCloudProposal(BranchState s,JsonObject u,string type,JsonObject p,out JsonObject result)
    {
        result=new();if(type is not ("commerce.configure" or "commerce.decide"))return false;Role(u,"manager");
        if(type=="commerce.configure"){var w=s.Get("user",p.S("waiterId"));Need(w.B("active")&&w.S("role")=="mesero","Responsable activo requerido");s.Meta["tenantId"]=Text(p,"tenantId",64);s.Meta["branchId"]=Text(p,"branchId",64);s.Meta["cloudWaiterId"]=w.S("id");s.Meta["cloudOrdersEnabled"]=p.B("enabled");result=J.O("configured",true);return true;}
        var proposal=s.Get("cloudproposal",p.S("id"));Need(proposal.S("status")=="pending-review","Propuesta ya resuelta",409);var reason=Text(p,"reason");var command=proposal.Obj("command");var payload=command.Obj("payload");string visitId="";
        if(p.B("accept")){
            if(command.S("type")=="invoice"){var receiver=(JsonObject)payload.Obj("receiver").DeepClone();receiver["visitId"]=payload.S("visitId");var fiscal=Act(s,u,"fiscal.request",receiver);visitId=fiscal.S("visitId");}
            else{
                Need(payload.S("paymentMethod")=="cash","Pago en línea pendiente de verificación independiente; no se registra como cobrado");var customer=Act(s,u,"customer.save",J.O("name",payload.S("name"),"phone",payload.S("phone")));string address="";
                if(payload.S("mode")=="delivery"){var a=(JsonObject)payload.Obj("address").DeepClone();a["customerId"]=customer.S("id");a["label"]="Pedido web";address=Act(s,u,"address.save",a).S("id");}
                var delivery=Act(s,u,"delivery.create",J.O("customerId",customer.S("id"),"mode",payload.S("mode"),"addressId",address,"waiterId",s.Meta.S("cloudWaiterId")));visitId=delivery.S("visitId");var visit=s.Get("visit",visitId);visit["priceListId"]="base";Act(s,u,"command.send",J.O("visitId",visitId,"items",payload.A("items")));
                foreach(var row in payload.A("items").Objects())Need(s.Related("unit","visitId",visitId).Where(x=>x.S("productId")==row.S("productId")).All(x=>x.L("price")==row.L("quotedPrice")),"Precio local cambió; rechaza y solicita nueva cotización",409);
                if(payload.L("discount")>0){var promo=s.List("promotion").FirstOrDefault(x=>x.B("active")&&x.S("coupon")==payload.S("coupon"));Need(promo is not null&&promo.L("percent")<=s.Meta.L("discountLimit",30),"Cupón sin autorización local");var hour=TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,TimeZoneInfo.FindSystemTimeZoneById("America/Mexico_City")).Hour;Need(hour>=promo!.L("startHour")&&hour<promo.L("endHour"),"Cupón fuera de horario local");Need(payload.A("items").Objects().All(x=>promo!.A("productIds").Strings().Contains(x.S("productId"))),"Cupón no aplicable");var gross=s.Related("unit","visitId",visitId).Sum(x=>x.L("price"));Need((long)Math.Round(gross*promo!.L("percent")/100m,MidpointRounding.AwayFromZero)==payload.L("discount"),"Cupón cambió");visit["discount"]=J.O("kind","amount","value",payload.L("discount"),"approvedBy",u.S("id"),"reason",reason);}
                Need(Totals(s,visit).L("total")==payload.L("quotedTotal")&&visit.L("deliveryFee")==payload.L("deliveryFee"),"Importe o tarifa local cambió; rechaza y solicita nueva cotización",409);visit["cloudOrderId"]=proposal.S("id");
            }
        }
        proposal["status"]=p.B("accept")?"accepted-local":"rejected-local";proposal["decidedBy"]=u.S("id");proposal["reason"]=reason;proposal["ack"]=J.O("id",proposal.S("id"),"digest",proposal.S("digest"),"status",proposal.S("status"),"visitId",visitId,"at",J.Now());result=proposal.Obj("ack");return true;
    }
}
