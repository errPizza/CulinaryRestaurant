using System.Globalization;
using System.Text.Json.Nodes;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;
public sealed partial class Restaurant
{
    public static decimal Coordinate(JsonObject p,string key,decimal limit)
    {
        decimal x;try{x=p[key] is JsonValue j&&j.TryGetValue<string>(out var text)?decimal.Parse(text,CultureInfo.InvariantCulture):p[key]!.GetValue<decimal>();}catch{throw new BusinessException(400,"Coordenada inválida");}Need(x>=-limit&&x<=limit,"Coordenada fuera de rango");return x;
    }
    public static bool InPolygon(JsonArray polygon,decimal lat,decimal lon)
    {
        var vertices=polygon.Objects().ToArray();bool inside=false;for(int i=0,j=vertices.Length-1;i<vertices.Length;j=i++){
            var a=vertices[i];var b=vertices[j];var ay=Coordinate(a,"lat",90);var ax=Coordinate(a,"lon",180);var by=Coordinate(b,"lat",90);var bx=Coordinate(b,"lon",180);
            var cross=(lon-ax)*(by-ay)-(lat-ay)*(bx-ax);if(cross==0&&lon>=Math.Min(ax,bx)&&lon<=Math.Max(ax,bx)&&lat>=Math.Min(ay,by)&&lat<=Math.Max(ay,by))return true;
            if((ay>lat)!=(by>lat)&&lon<(bx-ax)*(lat-ay)/(by-ay)+ax)inside=!inside;
        }return inside;
    }
    bool TryDelivery(BranchState s,JsonObject u,string type,JsonObject p,out JsonObject result)
    {
        result=J.O("ok",true);if(!new[]{"customer.save","address.save","delivery.create","delivery.assign","delivery.dispatch","delivery.reconcile","delivery.pickup","courier.save","zone.save","station.configure"}.Contains(type))return false;Role(u,"manager","mesero");var id=p.S("id");
        switch(type){
        case "customer.save":{
            var customer=id==""?Add(s,"customer",new()):s.Get("customer",id);customer["name"]=Text(p,"name",150);customer["phone"]=Text(p,"phone",40);customer["notes"]=p.S("notes")[..Math.Min(p.S("notes").Length,1000)];result=customer;break;}
        case "address.save":{
            var customer=s.Get("customer",p.S("customerId"));var address=id==""?Add(s,"address",J.O("customerId",customer.S("id"))):s.Get("address",id);Need(address.S("customerId")==customer.S("id"),"Dirección de otro cliente");address["label"]=Text(p,"label",100);address["text"]=Text(p,"text",500);
            if(p.B("geocode")){var point=Geocode(s,address.S("text"));address["lat"]=point["lat"]!.DeepClone();address["lon"]=point["lon"]!.DeepClone();address["coordinateSource"]="simulated-geocoder";}
            else{address["lat"]=Coordinate(p,"lat",90);address["lon"]=Coordinate(p,"lon",180);address["coordinateSource"]="manual";}result=address;break;}
        case "courier.save":{Role(u,"manager");var courier=id==""?Add(s,"courier",new()):s.Get("courier",id);courier["name"]=Text(p,"name",100);courier["active"]=p.B("active",true);result=courier;break;}
        case "zone.save":{
            Role(u,"manager");var polygon=p.A("polygon");Need(polygon.Count is >=3 and <=50&&polygon.Objects().Count()==polygon.Count,"Polígono de 3 a 50 vértices");foreach(var vertex in polygon.Objects()){Coordinate(vertex,"lat",90);Coordinate(vertex,"lon",180);}Need(polygon.Objects().Select(x=>J.Canonical(x)).Distinct().Count()>=3,"Vértices distintos requeridos");
            var zone=id==""?Add(s,"zone",new()):s.Get("zone",id);zone["name"]=Text(p,"name",100);zone["polygon"]=polygon.DeepClone();zone["fee"]=Amount(p,"fee");zone["priority"]=Amount(p,"priority",0,1000);zone["active"]=p.B("active",true);result=zone;break;}
        case "delivery.create":{
            var customer=s.Get("customer",p.S("customerId"));var mode=Choice(p,"mode","delivery","pickup");JsonObject? address=null,zone=null;
            if(mode=="delivery"){address=s.Get("address",p.S("addressId"));Need(address.S("customerId")==customer.S("id"),"Dirección de otro cliente");zone=s.List("zone").Where(x=>x.B("active")&&InPolygon(x.A("polygon"),Coordinate(address,"lat",90),Coordinate(address,"lon",180))).OrderBy(x=>x.L("priority")).ThenBy(x=>x.S("id"),StringComparer.Ordinal).FirstOrDefault();Need(zone is not null,"Dirección fuera de cobertura configurada");}
            var visit=Act(s,u,"visit.quick",J.O("waiterId",p.S("waiterId",u.S("id"))));var v=s.Get("visit",visit.S("id"));v["channel"]=mode;v["customerId"]=customer.S("id");v["deliveryFee"]=zone?.L("fee")??0;
            JsonNode? pickupAt=null;if(mode=="pickup"&&p.S("pickupAt")!=""){Need(DateTimeOffset.TryParse(p.S("pickupAt"),out var at)&&at>DateTimeOffset.UtcNow.AddMinutes(-5)&&at<DateTimeOffset.UtcNow.AddDays(7),"Hora de recogida inválida (hasta 7 días)");pickupAt=at.ToUniversalTime().ToString("O");}v["customerName"]=customer.S("name");
            result=Add(s,"delivery",J.O("pickupAt",pickupAt,"pickupName",Sanitize(p.S("pickupName",customer.S("name")),80),"visitId",v.S("id"),"customerId",customer.S("id"),"mode",mode,"addressSnapshot",address,"zoneId",zone?.S("id"),"fee",zone?.L("fee")??0,"status","accepted-local","at",J.Now(),"responsibility",0,"reconciled",0));break;}
        case "delivery.assign":{
            Role(u,"manager");var d=s.Get("delivery",id);Need(d.S("mode")=="delivery"&&d.S("status")=="accepted-local","Pedido no asignable");var c=s.Get("courier",p.S("courierId"));Need(c.B("active"),"Repartidor inactivo");var v=s.Get("visit",d.S("visitId"));Need(v.S("status")=="open"&&!s.Related("unit","visitId",v.S("id")).Any(x=>!new[]{"ready","delivered","cancelled","unavailable"}.Contains(x.S("status"))),"Termina la preparación antes de asignar");d["courierId"]=c.S("id");d["responsibility"]=Totals(s,v).L("balance");d["status"]="assigned";v["printed"]=true;d["assignedAt"]=J.Now();break;}
        case "delivery.dispatch":{
            var d=s.Get("delivery",id);Own(s,u,d.S("visitId"));Need(d.S("status")=="assigned","Asigna un repartidor");Act(s,u,"visit.deliverAll",J.O("id",d.S("visitId")));d["status"]="out-for-delivery";d["dispatchedAt"]=J.Now();break;}
        case "delivery.pickup":{
            var d=s.Get("delivery",id);Need(d.S("mode")=="pickup"&&d.S("status")=="accepted-local","Pedido no disponible para recogida");Own(s,u,d.S("visitId"));Need(!s.Related("unit","visitId",d.S("visitId")).Any(x=>!new[]{"ready","delivered","cancelled","unavailable"}.Contains(x.S("status"))),"Termina preparación");Act(s,u,"visit.deliverAll",J.O("id",d.S("visitId")));d["status"]="picked-up";d["pickedUpAt"]=J.Now();break;}
        case "delivery.reconcile":{
            Role(u,"manager");var d=s.Get("delivery",id);Need(d.S("status")=="out-for-delivery","Reparto sin salida pendiente");var amount=Amount(p,"amount",1);Need(amount<=d.L("responsibility")-d.L("reconciled"),"Supera la responsabilidad pendiente");var method=Choice(p,"method","cash","card","transfer");Text(p,"reference",200);var pay=Act(s,u,"payment.add",J.O("visitId",d.S("visitId"),"amount",amount,"method",method));d["reconciled"]=d.L("reconciled")+amount;Add(s,"ownernotice",J.O("type","courier-reconciliation","deliveryId",id,"paymentId",pay.S("id"),"reference",p.S("reference"),"verification",method=="cash"?"counted-cash":"manual-voucher-attestation","actor",u.S("id"),"status","pending","at",J.Now()));if(d.L("reconciled")==d.L("responsibility"))d["status"]="reconciled";break;}
        case "station.configure":{
            Role(u,"manager");var name=Text(p,"name",100);Need(s.Meta.A("stations").Strings().Contains(name),"Estación no configurada");var station=s.Related("station","name",name).FirstOrDefault()??Add(s,"station",J.O("name",name));var yellow=Amount(p,"yellowMinutes",1,120);var red=Amount(p,"redMinutes",yellow+1,240);station["yellowMinutes"]=yellow;station["redMinutes"]=red;break;}
        }return true;
    }
}
