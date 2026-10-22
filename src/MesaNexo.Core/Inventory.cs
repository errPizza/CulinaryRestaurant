using System.Globalization;
using System.Text.Json.Nodes;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;
public sealed partial class Restaurant
{
    public static long ExactQuantity(JsonObject p,string key="quantity")
    {
        decimal value;try{value=p[key] is JsonValue j&&j.TryGetValue<string>(out var text)?decimal.Parse(text,CultureInfo.InvariantCulture):p[key]!.GetValue<decimal>();}catch{throw new BusinessException(400,"Cantidad decimal inválida");}
        var factor=p.S("unit","base") switch{"kg" or "l"=>1_000_000m,"g" or "ml" or "unit" or "base"=>1000m,"mg"=>1m,"dozen"=>12000m,_=>throw new BusinessException(400,"Unidad no configurada")};
        var raw=checked(value*factor);Need(raw>0&&raw<=1_000_000_000_000m&&raw==decimal.Truncate(raw),"Cantidad fuera de precisión (1/1000 de unidad base)");return (long)raw;
    }
    void ValidateIngredientUnit(JsonObject ingredient,string unit)
    {
        var family=ingredient.S("unit");Need(family=="g"?new[]{"g","kg","mg","base"}.Contains(unit):family=="ml"?new[]{"ml","l","base"}.Contains(unit):new[]{"unit","dozen","base"}.Contains(unit),"Conversión incompatible con el insumo");
    }
    JsonObject Stock(BranchState s,string warehouse,string ingredient)
    {
        s.Get("warehouse",warehouse);s.Get("ingredient",ingredient);var id=warehouse+"-"+ingredient;return s.List("stock").FirstOrDefault(x=>x.S("id")==id)??Add(s,"stock",J.O("id",id,"warehouseId",warehouse,"ingredientId",ingredient,"quantity",0,"reserved",0,"value",0));
    }
    Dictionary<string,long> Explode(BranchState s,string versionId,long servings,HashSet<string>? path=null)
    {
        path??=[];Need(path.Add(versionId),"Ciclo en subrecetas");var v=s.Get("recipeversion",versionId);var result=new Dictionary<string,long>();
        foreach(var line in v.A("lines").Objects()){
            var numerator=checked((decimal)line.L("quantity")*servings);var raw=numerator/v.L("yield");Need(raw==decimal.Truncate(raw),"La receta produce fracciones menores a la precisión base; aumenta el rendimiento o ajusta cantidades");var qty=checked((long)raw);
            if(line.S("ingredientId")!="")result[line.S("ingredientId")]=checked(result.GetValueOrDefault(line.S("ingredientId"))+qty);
            else foreach(var child in Explode(s,line.S("versionId"),qty,new(path)))result[child.Key]=checked(result.GetValueOrDefault(child.Key)+child.Value);
        }return result;
    }
    void RecipeCycle(BranchState s,string recipeId,JsonArray lines,HashSet<string>? seen=null)
    {
        seen??=[];Need(seen.Add(recipeId),"Ciclo de recetas");foreach(var line in lines.Objects().Where(x=>x.S("recipeId")!="")){var child=line.S("recipeId");Need(!seen.Contains(child),"Ciclo de recetas");var recipe=s.Get("recipe",child);RecipeCycle(s,child,s.Get("recipeversion",recipe.S("versionId")).A("lines"),new(seen));}
    }
    void ReserveUnit(BranchState s,JsonObject unit)
    {
        if(unit.S("recipeVersionId")=="")return;Need(s.Meta.S("inventoryPolicy")=="reserve-on-send-consume-on-close","Configura reserva al enviar y consumo al liquidar");var warehouse=s.Meta.S("saleWarehouseId");var lines=Explode(s,unit.S("recipeVersionId"),1000);Need(lines.Count<=30,"Máximo 30 insumos por porción");
        foreach(var line in lines){var stock=Stock(s,warehouse,line.Key);Need(stock.L("quantity")-stock.L("reserved")>=line.Value,"Stock insuficiente: "+s.Get("ingredient",line.Key).S("name"));stock["reserved"]=checked(stock.L("reserved")+line.Value);}
        var prior=s.List("unitstock").FirstOrDefault(x=>x.S("id")==unit.S("id"));var cycle=(prior?.L("cycle")??0)+1;var data=J.O("id",unit.S("id"),"unitId",unit.S("id"),"visitId",unit.S("visitId"),"warehouseId",warehouse,"recipeVersionId",unit.S("recipeVersionId"),"cycle",cycle,"status","reserved","prepared",false,"at",J.Now(),"lines",lines.Select(l=>J.O("ingredientId",l.Key,"quantity",l.Value)).ToArray());
        if(prior is null)Add(s,"unitstock",data);else{data["siteId"]=s.Meta.S("siteId");data["kind"]="unitstock";s.Records[BranchState.Key("unitstock",unit.S("id"))]=data;}
    }
    void ReleaseOrWaste(BranchState s,JsonObject reservation,string reason)
    {
        if(reservation.S("status")!="reserved")return;
        if(reservation.B("prepared")){
            Add(s,"stockjob",J.O("id","waste-"+reservation.S("id")+"-"+reservation.L("cycle"),"unitId",reservation.S("unitId"),"visitId",reservation.S("visitId"),"warehouseId",reservation.S("warehouseId"),"status","pending","movementType","prepared-waste","reason",reason,"at",J.Now(),"lines",reservation.A("lines")));reservation["status"]="waste-queued";
        }else{
            foreach(var line in reservation.A("lines").Objects()){var stock=Stock(s,reservation.S("warehouseId"),line.S("ingredientId"));stock["reserved"]=stock.L("reserved")-line.L("quantity");}reservation["status"]="released-unprepared";
        }
        reservation["reason"]=reason;
    }
    void InventoryAfterAction(BranchState s,JsonObject u,string type,JsonObject p,JsonObject result)
    {
        if(type=="command.send")foreach(var uid in result.A("unitIds").Strings())ReserveUnit(s,s.Get("unit",uid));
        if(type=="unit.state"){
            var unit=s.Get("unit",p.S("id"));var reservation=s.List("unitstock").FirstOrDefault(x=>x.S("unitId")==unit.S("id"));if(reservation is not null){if(unit.S("status") is "preparing" or "ready" or "delivered")reservation["prepared"]=true;if(unit.S("status")=="unavailable")ReleaseOrWaste(s,reservation,unit.S("reason"));}
        }
        if(type=="request.decide"&&p.B("approve")){
            var request=s.Get("request",p.S("id"));if(request.S("type") is "cancel" or "change"){
                var unit=s.Get("unit",request.S("unitId"));var reservation=s.List("unitstock").FirstOrDefault(x=>x.S("unitId")==unit.S("id"));if(reservation is not null)ReleaseOrWaste(s,reservation,request.S("reason"));if(request.S("type")=="change")ReserveUnit(s,unit);
            }
        }
        if(type is "visit.split" or "visit.merge")foreach(var reservation in s.List("unitstock").Where(x=>x.S("status")=="reserved"))reservation["visitId"]=s.Get("unit",reservation.S("unitId")).S("visitId");
    }
    void QueueConsumption(BranchState s,JsonObject v)
    {
        var units=s.List("unit").Where(x=>x.S("visitId")==v.S("id")&&x.S("status")=="delivered"&&x.S("recipeVersionId")!="").ToArray();if(units.Length==0)return;
        var reservations=units.Select(unit=>s.Get("unitstock",unit.S("id"))).Where(x=>x.S("status")=="reserved").ToArray();
        foreach(var group in reservations.GroupBy(x=>x.S("warehouseId"))){var lines=new Dictionary<string,long>();foreach(var reservation in group)foreach(var line in reservation.A("lines").Objects())lines[line.S("ingredientId")]=checked(lines.GetValueOrDefault(line.S("ingredientId"))+line.L("quantity"));Need(lines.Count<=40,"Máximo 40 insumos distintos por trabajo");
            Add(s,"stockjob",J.O("id","sale-"+v.S("id")+"-"+group.Key+"-"+v.L("closeCycle"),"visitId",v.S("id"),"warehouseId",group.Key,"status","pending","movementType","sale","at",J.Now(),"recipeVersions",group.Select(x=>x.S("recipeVersionId")).Distinct().ToArray(),"lines",lines.Select(l=>J.O("ingredientId",l.Key,"quantity",l.Value)).ToArray()));foreach(var reservation in group)reservation["status"]="sale-queued";
        }
    }
    JsonObject Consume(BranchState s,string warehouse,string ingredient,long quantity,string type,string source,string actor,bool reserved=false)
    {
        var stock=Stock(s,warehouse,ingredient);Need(stock.L("quantity")-(reserved?0:stock.L("reserved"))>=quantity,"Stock no disponible");var layers=s.List("stocklayer").Where(x=>x.S("warehouseId")==warehouse&&x.S("ingredientId")==ingredient&&x.L("remaining")>0).OrderBy(x=>x.L("order")).ThenBy(x=>x.S("id"),StringComparer.Ordinal).ToArray();var remaining=quantity;long fifoCost=0;var trace=new JsonArray();
        foreach(var layer in layers){if(remaining==0)break;var take=Math.Min(remaining,layer.L("remaining"));var cost=take==layer.L("remaining")?layer.L("remainingCost"):(long)Math.Round(layer.L("remainingCost")*(decimal)take/layer.L("remaining"),MidpointRounding.AwayFromZero);layer["remaining"]=layer.L("remaining")-take;layer["remainingCost"]=layer.L("remainingCost")-cost;fifoCost=checked(fifoCost+cost);trace.Add(J.O("layerId",layer.S("id"),"quantity",take,"fifoCost",cost));remaining-=take;}
        Need(remaining==0,"Capas de inventario inconsistentes",409);var method=s.Get("warehouse",warehouse).S("costMethod","average");var costTotal=method=="fifo"?fifoCost:quantity==stock.L("quantity")?stock.L("value"):(long)Math.Round(stock.L("value")*(decimal)quantity/stock.L("quantity"),MidpointRounding.AwayFromZero);
        stock["quantity"]=stock.L("quantity")-quantity;stock["value"]=stock.L("value")-costTotal;if(reserved)stock["reserved"]=stock.L("reserved")-quantity;
        return Add(s,"stockmovement",J.O("warehouseId",warehouse,"ingredientId",ingredient,"quantity",-quantity,"cost",-costTotal,"costMethod",method,"type",type,"sourceId",source,"actor",actor,"at",J.Now(),"layers",trace));
    }
    void ReceiveStock(BranchState s,string warehouse,string ingredient,long quantity,long cost,string type,string source,string actor)
    {
        var stock=Stock(s,warehouse,ingredient);stock["quantity"]=checked(stock.L("quantity")+quantity);stock["value"]=checked(stock.L("value")+cost);s.Meta["layerOrder"]=s.Meta.L("layerOrder")+1;
        var layer=Add(s,"stocklayer",J.O("warehouseId",warehouse,"ingredientId",ingredient,"received",quantity,"remaining",quantity,"originalCost",cost,"remainingCost",cost,"order",s.Meta.L("layerOrder"),"sourceId",source,"at",J.Now()));
        Add(s,"stockmovement",J.O("warehouseId",warehouse,"ingredientId",ingredient,"quantity",quantity,"cost",cost,"type",type,"sourceId",source,"layerId",layer.S("id"),"actor",actor,"at",J.Now()));
    }
    bool TryInventory(BranchState s,JsonObject u,string type,JsonObject p,out JsonObject result)
    {
        result=J.O("ok",true);if(!new[]{"inventory.","recipe.","stock.","transfer."}.Any(type.StartsWith))return false;Role(u,"manager");var id=p.S("id");
        switch(type){
        case "inventory.configure":{var w=s.Get("warehouse",p.S("warehouseId"));s.Meta["saleWarehouseId"]=w.S("id");s.Meta["inventoryPolicy"]=Choice(p,"policy","reserve-on-send-consume-on-close");break;}
        case "inventory.warehouse":{
            var w=id==""?Add(s,"warehouse",new()):s.Get("warehouse",id);var method=Choice(p,"costMethod","average","fifo");Need(w.S("costMethod",method)==method||!s.List("stock").Any(x=>x.S("warehouseId")==id&&x.L("quantity")!=0),"Cambia método sólo con almacén vacío");w["name"]=Text(p,"name",100);w["costMethod"]=method;result=w;break;}
        case "inventory.ingredient":{var i=id==""?Add(s,"ingredient",new()):s.Get("ingredient",id);var unit=Choice(p,"unit","g","ml","unit");Need(i.S("unit",unit)==unit,"La unidad base histórica no se modifica");i["name"]=Text(p,"name",100);i["unit"]=unit;result=i;break;}
        case "recipe.save":{
            var recipe=id==""?Add(s,"recipe",J.O("name",Text(p,"name",100))):s.Get("recipe",id);var lines=new JsonArray();Need(p.A("lines").Count is >0 and <=30,"Receta: de 1 a 30 renglones");
            foreach(var line in p.A("lines").Objects()){
                Need((line.S("ingredientId")=="")!=(line.S("recipeId")==""),"Renglón de insumo o subreceta");var quantity=ExactQuantity(line);if(line.S("ingredientId")!=""){ValidateIngredientUnit(s.Get("ingredient",line.S("ingredientId")),line.S("unit","base"));lines.Add(J.O("ingredientId",line.S("ingredientId"),"quantity",quantity));}
                else{var child=s.Get("recipe",line.S("recipeId"));lines.Add(J.O("recipeId",child.S("id"),"versionId",child.S("versionId"),"quantity",quantity));}}
            RecipeCycle(s,recipe.S("id"),lines);var yield=ExactQuantity(J.O("quantity",p["yield"],"unit","unit"));var version=Add(s,"recipeversion",J.O("recipeId",recipe.S("id"),"number",recipe.L("version")+1,"yield",yield,"lines",lines,"at",J.Now(),"actor",u.S("id")));recipe["versionId"]=version.S("id");recipe["version"]=version.L("number");recipe["name"]=Text(p,"name",100);result=version;break;}
        case "recipe.bind":{var product=s.Get("product",p.S("productId"));var recipe=s.Get("recipe",p.S("recipeId"));product["recipeVersionId"]=recipe.S("versionId");result=product;break;}
        case "stock.receive":{var i=s.Get("ingredient",p.S("ingredientId"));ValidateIngredientUnit(i,p.S("unit","base"));var qty=ExactQuantity(p);var reference=Text(p,"reference",200);ReceiveStock(s,p.S("warehouseId"),i.S("id"),qty,Amount(p,"cost"),"receipt",reference,u.S("id"));break;}
        case "stock.waste":{var i=s.Get("ingredient",p.S("ingredientId"));ValidateIngredientUnit(i,p.S("unit","base"));result=Consume(s,p.S("warehouseId"),i.S("id"),ExactQuantity(p),"waste",Text(p,"reason"),u.S("id"));break;}
        case "stock.return":{
            var original=s.Get("stockmovement",p.S("movementId"));Need(original.L("quantity")<0&&original.S("type")=="sale","Sólo devolución física de consumo registrado");Need(!s.List("stockmovement").Any(x=>x.S("type")=="return"&&x.S("sourceId")==original.S("id")),"Consumo ya devuelto");Text(p,"reason");ReceiveStock(s,original.S("warehouseId"),original.S("ingredientId"),-original.L("quantity"),-original.L("cost"),"return",original.S("id"),u.S("id"));break;}
        case "stock.process":{
            var job=s.Get("stockjob",id);if(job.S("status")=="done"){result=J.O("duplicate",true);break;}Need(job.S("status")=="pending","Trabajo inválido");
            var line=job.A("lines").Objects().FirstOrDefault(x=>x.L("remaining",x.L("quantity"))>0);
            if(line is not null){var left=line.L("remaining",line.L("quantity"));var batch=s.List("stocklayer").Where(x=>x.S("warehouseId")==job.S("warehouseId")&&x.S("ingredientId")==line.S("ingredientId")&&x.L("remaining")>0).OrderBy(x=>x.L("order")).Take(20).Sum(x=>x.L("remaining"));var take=Math.Min(left,batch);Need(take>0,"Capas insuficientes para consumo reservado");Consume(s,job.S("warehouseId"),line.S("ingredientId"),take,job.S("movementType","sale"),id,u.S("id"),true);line["remaining"]=left-take;}
            if(job.A("lines").Objects().All(x=>x.L("remaining",x.L("quantity"))==0)){job["status"]="done";job["completedAt"]=J.Now();}break;}
        case "transfer.create":{
            var from=p.S("fromWarehouseId");var to=p.S("toWarehouseId");Need(from!=to,"Almacenes distintos");s.Get("warehouse",from);s.Get("warehouse",to);var i=s.Get("ingredient",p.S("ingredientId"));ValidateIngredientUnit(i,p.S("unit","base"));var qty=ExactQuantity(p);var stock=Stock(s,from,i.S("id"));Need(stock.L("quantity")-stock.L("reserved")>=qty,"Stock insuficiente");stock["reserved"]=stock.L("reserved")+qty;
            result=Add(s,"transfer",J.O("fromWarehouseId",from,"toWarehouseId",to,"ingredientId",i.S("id"),"quantity",qty,"status","sender-approved","senderId",u.S("id"),"reason",Text(p,"reason"),"at",J.Now()));break;}
        case "transfer.receive":{
            var t=s.Get("transfer",id);Need(t.S("status")=="sender-approved"&&t.S("senderId")!=u.S("id"),"Debe recibir otra persona identificada",403);var movement=Consume(s,t.S("fromWarehouseId"),t.S("ingredientId"),t.L("quantity"),"transfer-out",id,u.S("id"),true);ReceiveStock(s,t.S("toWarehouseId"),t.S("ingredientId"),t.L("quantity"),-movement.L("cost"),"transfer-in",id,u.S("id"));t["receiverId"]=u.S("id");t["receivedAt"]=J.Now();t["status"]="received";break;}
        case "transfer.cancel":{var t=s.Get("transfer",id);Need(t.S("status")=="sender-approved","Traspaso resuelto");Text(p,"reason");var stock=Stock(s,t.S("fromWarehouseId"),t.S("ingredientId"));stock["reserved"]=stock.L("reserved")-t.L("quantity");t["status"]="cancelled";t["cancelledBy"]=u.S("id");break;}
        default:throw new BusinessException(404,"Operación de inventario desconocida");}
        return true;
    }
}
