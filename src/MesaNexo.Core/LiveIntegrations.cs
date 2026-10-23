using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;
// Live integrations: what the domain queues for the server's services and what it does with their answers. The rules are the
// simulators' (Integrations.cs); only who answers changes.
public sealed partial class Restaurant
{
    // ---------- terminals ----------
    // A charge on its way or without an answer blocks another one on the same account.
    static readonly string[] TerminalBusy=["queued","sending","waiting","cancel-requested","uncertain"];
    static readonly string[] CardBrands=["Visa","Mastercard","American Express","Carnet","Otra"];
    // Connected terminals (Mercado Pago Point, Clip) or manual ones (BBVA: charged on the terminal, recorded from the voucher).
    JsonObject TerminalOf(string id,bool manual)
    {
        var all=LiveCatalog.A("terminals").Objects().Where(t=>t.B("manual")==manual).ToArray();
        Need(all.Length>0,manual?"No hay terminales BBVA (semi-integradas) en terminal.json":"No hay terminales conectadas en terminal.json");
        if(id==""&&all.Length==1)return all[0];
        return all.FirstOrDefault(t=>t.S("id")==id)??throw new BusinessException(400,"Elige la terminal");
    }
    void ChargeAllowed(BranchState s,JsonObject v,long amount)
    {
        Need(amount<=Totals(s,v).L("balance"),"El pago supera el saldo");Need(s.Related("cash","status","open").Any(),"Abre una caja antes de registrar pagos");
        Need(!s.Related("terminaltxn","visitId",v.S("id")).Any(x=>TerminalBusy.Contains(x.S("status"))),"Hay un cobro con terminal en curso en esta cuenta; espera su resultado o consúltalo antes de cobrar otra vez");
    }
    // The charge is queued; the terminal service sends it to the device and the result arrives through IntegrationResult.
    JsonObject LiveCharge(BranchState s,JsonObject u,JsonObject p)
    {
        var v=Own(s,u,p.S("visitId"));var terminal=TerminalOf(p.S("terminalId"),false);var amount=Amount(p,"amount",1);var tip=Amount(p,"tip");ChargeAllowed(s,v,amount);
        var txn=Add(s,"terminaltxn",J.O("visitId",v.S("id"),"folioId",p.S("folioId"),"amount",amount,"tip",tip,"terminalId",terminal.S("id"),"terminalName",terminal.S("name"),"provider",terminal.S("provider"),"reference",Ref("CR"),"status","queued","live",true,"message","Enviando a la terminal","actor",u.S("id"),"at",J.Now()));
        Log(s,u,"terminal","charge","queued",terminal.S("name")+" · "+Pesos(amount+tip),txn.S("reference"),simulated:false);return txn;
    }
    // BBVA and any terminal without an API: the card was charged on the terminal and its voucher gives the authorization.
    JsonObject ManualCharge(BranchState s,JsonObject u,JsonObject p)
    {
        Need(IsLive(s,"terminal"),"Activa la terminal bancaria en modo real en Integraciones");var v=Own(s,u,p.S("visitId"));var terminal=TerminalOf(p.S("terminalId"),true);var amount=Amount(p,"amount",1);var tip=Amount(p,"tip");ChargeAllowed(s,v,amount);
        var auth=Text(p,"authCode",8).ToUpperInvariant();Need(Regex.IsMatch(auth,"^[A-Z0-9]{4,8}$"),"Autorización del voucher: de 4 a 8 letras o números");var last4=Text(p,"last4",4);Need(Regex.IsMatch(last4,@"^\d{4}$"),"Últimos 4 dígitos de la tarjeta");
        var txn=Add(s,"terminaltxn",J.O("visitId",v.S("id"),"folioId",p.S("folioId"),"amount",amount,"tip",tip,"terminalId",terminal.S("id"),"terminalName",terminal.S("name"),"provider",terminal.S("provider"),"brand",Choice(p,"brand",CardBrands),"last4",last4,"reference",Ref("MAN"),"manual",true,"live",true,"actor",u.S("id"),"at",J.Now()));
        ApproveTerminal(s,u,txn,auth,simulated:false);Log(s,u,"terminal","manual","approved",terminal.S("name")+" · "+txn.S("brand")+" •••• "+last4+" · aut. "+auth+" · "+Pesos(amount+tip),txn.S("reference"),simulated:false);return txn;
    }
    JsonObject LiveCancel(BranchState s,JsonObject u,JsonObject txn)
    {
        Need(txn.B("live"),"Ese cobro no es de una terminal real");var v=s.Get("visit",txn.S("visitId"));Need(u.S("role")=="manager"||v.S("waiterId")==u.S("id"),"La cuenta pertenece a otro mesero",403);
        if(txn.S("status")=="queued"){txn["status"]="cancelled";txn["message"]="Cancelado antes de llegar a la terminal";}
        else{Need(txn.S("status")=="waiting","Ese cobro ya no se puede cancelar desde aquí");txn["status"]="cancel-requested";txn["message"]="Cancelando en la terminal";}
        txn["cancelledBy"]=u.S("id");Log(s,u,"terminal","cancel",txn.S("status"),txn.S("terminalName")+" · "+Pesos(txn.L("amount")+txn.L("tip")),txn.S("reference"),simulated:false);return txn;
    }
    // A charge without an answer: if the provider gave its id, the terminal service asks again; if not, whoever has the
    // terminal's report says what happened (and the voucher's authorization when it was charged).
    JsonObject LiveReconcile(BranchState s,JsonObject u,JsonObject txn,JsonObject p)
    {
        Need(txn.S("status")=="uncertain","Ese cobro ya tiene resultado");var v=s.Get("visit",txn.S("visitId"));Need(u.S("role")=="manager"||v.S("waiterId")==u.S("id"),"La cuenta pertenece a otro mesero",403);
        if(txn.S("providerRef")!=""&&p.S("outcome")==""){txn["status"]="waiting";txn["message"]="Consultando a la terminal";return txn;}
        var outcome=Choice(p,"outcome","charged","not-charged");txn["reconciledBy"]=u.S("id");txn["reconciledAt"]=J.Now();
        if(outcome=="not-charged"){txn["status"]="declined";txn["message"]="Sin cargo según el reporte de la terminal";}
        else{
            var auth=Text(p,"authCode",8).ToUpperInvariant();Need(Regex.IsMatch(auth,"^[A-Z0-9]{4,8}$"),"Autorización del voucher: de 4 a 8 letras o números");txn["last4"]=Text(p,"last4",4);Need(Regex.IsMatch(txn.S("last4"),@"^\d{4}$"),"Últimos 4 dígitos de la tarjeta");txn["brand"]=Choice(p,"brand",CardBrands);
            if(v.S("status")=="open"&&txn.L("amount")<=Totals(s,v).L("balance"))ApproveTerminal(s,u,txn,auth,simulated:false);else{txn["status"]="refund-queued";txn["authCode"]=auth;txn["message"]="La terminal cobró, pero la cuenta ya estaba cubierta: se devuelve el cargo";}
        }
        Log(s,u,"terminal","reconcile",txn.S("status"),"Cobro sin respuesta · "+Pesos(txn.L("amount")+txn.L("tip")),txn.S("reference"),simulated:false);return txn;
    }
    // Effects of a provider's answer, applied in the same commit that stores it (IntegrationResult).
    void LiveEffects(BranchState s,JsonObject u,string kind,JsonObject row)
    {
        switch(kind){
        case "terminaltxn":
            // Same rule as the simulator: a charge that arrives for an already covered account is returned, never applied twice.
            if(row.S("status")=="approved"&&row.S("paymentId")==""){
                var v=s.Get("visit",row.S("visitId"));
                if(v.S("status")=="open"&&row.L("amount")<=Totals(s,v).L("balance")&&s.Related("cash","status","open").Any()){ApproveTerminal(s,u,row,row.S("authCode"),simulated:false);if(row.B("counter"))v["prepaid"]=true;}
                else{row["status"]="refund-queued";row["message"]="La terminal cobró, pero la cuenta ya estaba cubierta o la caja cerrada: se devuelve el cargo";}
            }
            break;
        }
    }
}
