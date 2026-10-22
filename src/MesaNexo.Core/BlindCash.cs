using System.Text.Json.Nodes;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;
public sealed partial class Restaurant
{
    public static readonly long[] Denominations=[10,20,50,100,200,500,1000,2000,5000,10000,20000,50000,100000];
    bool TryCash(BranchState s,JsonObject u,string type,JsonObject p,out JsonObject result)
    {
        result=J.O("ok",true);if(!new[]{"cash.blind.open","cash.blind.close","cash.adjust"}.Contains(type))return false;
        var id=p.S("id");
        if(type=="cash.blind.open"){
            Role(u,"manager","cajero");Need(!s.List("cash").Any(x=>x.S("status")=="open"),"Ya hay una caja abierta");var operatorId=p.S("operatorId",u.S("id"));var user=s.Get("user",operatorId);Need(user.S("role")=="cajero"&&user.B("active"),"El arqueo requiere un operador cajero sin acceso a reportes");Need(u.S("role")=="manager"||operatorId==u.S("id"),"Operador de otro turno",403);
            var cash=Add(s,"cash",J.O("status","open","blind",true,"operatorId",operatorId,"initial",Amount(p,"initial"),"openedAt",J.Now(),"openedBy",u.S("id")));result=J.O("id",cash.S("id"),"status","open","blind",true);return true;
        }
        if(type=="cash.adjust"){
            Role(u,"manager");var cash=s.Get("cash",id);Need(cash.S("status")=="closed","Ajusta sólo un cierre confirmado");result=Add(s,"cashadjustment",J.O("cashId",id,"method",Choice(p,"method","cash","card","transfer"),"direction",Choice(p,"direction","increase","decrease"),"amount",Amount(p,"amount",1),"reason",Text(p,"reason"),"authorizer",u.S("id"),"actor",u.S("id"),"at",J.Now()));return true;
        }
        Role(u,"cajero");var c=s.Get("cash",id);Need(c.B("blind")&&c.S("operatorId")==u.S("id"),"Turno de otro operador",403);Need(c.S("status")=="open","El turno ya está cerrado",409);var denominations=p.A("denominations");Need(denominations.Count<=Denominations.Length&&denominations.Objects().Count()==denominations.Count,"Conteo inválido");Need(denominations.Objects().Select(x=>x.L("value")).Distinct().Count()==denominations.Count,"Denominaciones repetidas");long counted=0;
        foreach(var d in denominations.Objects()){Need(Denominations.Contains(d.L("value")),"Denominación MXN inválida");counted=checked(counted+checked(d.L("value")*Amount(d,"count",0,1000000)));}
        var payments=s.List("payment").Where(x=>x.S("cashId")==id).ToArray();var expectedCash=c.L("initial")+payments.Where(x=>x.S("method")=="cash").Sum(x=>x.L("amount")+x.L("tip"))+s.List("movement").Where(x=>x.S("cashId")==id).Sum(x=>x.S("type")=="income"?x.L("amount"):-x.L("amount"));
        var expectedCard=payments.Where(x=>x.S("method")=="card").Sum(x=>x.L("amount")+x.L("tip"));var expectedTransfer=payments.Where(x=>x.S("method")=="transfer").Sum(x=>x.L("amount")+x.L("tip"));
        c["status"]="closed";c["denominations"]=denominations.DeepClone();c["counted"]=counted;c["expected"]=expectedCash;c["difference"]=counted-expectedCash;c["card"]=J.O("counted",Amount(p,"card"),"expected",expectedCard,"difference",Amount(p,"card")-expectedCard);c["transfer"]=J.O("counted",Amount(p,"transfer"),"expected",expectedTransfer,"difference",Amount(p,"transfer")-expectedTransfer);c["closedAt"]=J.Now();c["closedBy"]=u.S("id");
        Add(s,"ownernotice",J.O("type","blind-cash-closed","cashId",id,"status","pending","at",J.Now()));result=J.O("id",id,"status","closed","notification","queued");return true;
    }
    void FilterBlindSnapshot(BranchState s,JsonObject u,JsonObject output)
    {
        if(u.S("role")!="cajero")return;foreach(var key in output.Select(p=>p.Key).ToArray())if(!new[]{"user","sessionId","siteId","restaurant"}.Contains(key))output.Remove(key);
        output["restaurant"]=J.O("name",s.Meta.S("name"),"revision",s.Meta.L("revision"),"modules",new JsonArray());
        output["cashs"]=new JsonArray(s.List("cash").Where(c=>c.B("blind")&&c.S("operatorId")==u.S("id")).TakeLast(50).Select(c=>(JsonNode)J.O("id",c.S("id"),"status",c.S("status"),"blind",true,"openedAt",c.S("openedAt"),"closedAt",c.S("closedAt"))).ToArray());
    }
}
