using System.Text.Json.Nodes;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;
// Stage 3: QR per table. The public page only proposes; calls become waiter notices, orders wait for the waiter.
public sealed partial class Restaurant
{
    static readonly string[] TableRequestKinds=["waiter","bill","order"];
    JsonObject? OpenVisitAt(BranchState s,string tableId)=>s.Related("visit","status","open").FirstOrDefault(v=>(v.S("tableId")==tableId||v.A("linkedTableIds").Strings().Contains(tableId)));
    // Called by the cloud inbox in the same commit that stores the proposal; never creates sales or payments.
    public void ImportTableRequest(BranchState s,JsonObject proposal)
    {
        var payload=proposal.Obj("command").Obj("payload");var kind=payload.S("kind");var table=s.Related("table","id",payload.S("tableId")).FirstOrDefault();
        if(table is null||!TableRequestKinds.Contains(kind)||table.S("qrHash")==""||table.S("qrHash")!=payload.S("qrHash")){Settle(proposal,"rejected-local","Código QR vencido o mesa inexistente","");return;}
        var visit=OpenVisitAt(s,table.S("id"));var name=table.S("name");
        if(kind=="order"){
            if(visit is null){Settle(proposal,"rejected-local","La mesa no tiene una cuenta abierta","");return;}
            proposal["tableId"]=table.S("id");proposal["visitId"]=visit.S("id");Notice(s,visit,name+" envió un pedido desde el QR · revisa y confirma","qr-order").Add("proposalId",proposal.S("id"));return;
        }
        var message=name+(kind=="bill"?" pide la cuenta (QR)":" llama al mesero (QR)");
        if(visit is not null)Notice(s,visit,message,"customer");else Add(s,"notification",J.O("visitId","","tableId",table.S("id"),"waiterId","","message",message,"type","customer","at",J.Now(),"attended",false));
        Settle(proposal,"accepted-local","Aviso entregado al personal",visit?.S("id")??"");
    }
    static void Settle(JsonObject proposal,string status,string reason,string visitId){proposal["status"]=status;proposal["reason"]=reason;proposal["ack"]=J.O("id",proposal.S("id"),"digest",proposal.S("digest"),"status",status,"visitId",visitId,"at",J.Now());}
    bool TryTableQr(BranchState s,JsonObject u,string type,JsonObject p,out JsonObject result)
    {
        result=J.O("ok",true);
        switch(type){
        case "qr.configure":{Role(u,"manager");var url=new Uri(Text(p,"url",500));Need(url.Scheme=="https"&&url.UserInfo==""&&url.Query==""&&url.Fragment=="","Dirección pública HTTPS sin parámetros requerida");s.Meta["tablePortal"]=url.ToString().TrimEnd('/');break;}
        case "table.qr":{
            Role(u,"manager");var table=s.Get("table",p.S("id"));Need(s.Meta.S("tablePortal")!="","Configura primero la dirección pública del restaurante");
            if(p.B("rotate")||table.S("qrToken")==""){var token=Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));table["qrToken"]=token;table["qrHash"]=J.Hash(token);table["qrRotatedAt"]=J.Now();}
            result=J.O("tableId",table.S("id"),"name",table.S("name"),"link",s.Meta.S("tablePortal")+"#mesa="+table.S("qrToken"),"rotatedAt",table.S("qrRotatedAt"));break;}
        case "qr.decide":{
            Role(u,"mesero","manager");var proposal=s.Get("cloudproposal",p.S("id"));Need(proposal.Obj("command").S("type")=="table"&&proposal.S("status")=="pending-review","Pedido QR ya resuelto",409);var visit=Own(s,u,proposal.S("visitId"));var payload=proposal.Obj("command").Obj("payload");
            if(p.B("accept")){
                var command=Act(s,u,"command.send",J.O("visitId",visit.S("id"),"items",payload.A("items").DeepClone()));
                foreach(var row in payload.A("items").Objects())Need(s.Related("unit","commandId",command.S("id")).Where(x=>x.S("productId")==row.S("productId")).All(x=>x.L("price")==row.L("quotedPrice")),"El precio cambió desde que el cliente lo vio; rechaza y pídele que lo envíe de nuevo",409);
                Settle(proposal,"accepted-local","Confirmado por "+u.S("name"),visit.S("id"));
            }else Settle(proposal,"rejected-local",Text(p,"reason",200),visit.S("id"));
            proposal["decidedBy"]=u.S("id");foreach(var n in s.Related("notification","proposalId",proposal.S("id")))n["attended"]=true;result=proposal.Obj("ack");break;}
        default:return false;
        }return true;
    }
}
