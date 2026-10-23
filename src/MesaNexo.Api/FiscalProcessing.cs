using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
public sealed partial class BranchService
{
    // SAT c_FormaPago for each local payment method; a branch may override them in "paymentForms". 99 is not valid with PUE.
    static readonly Dictionary<string,string> PaymentForms=new(){["cash"]="01",["card"]="04",["transfer"]="03",["wallet"]="05"};
    static readonly string[] SatPaymentForms=["01","02","03","04","05","06","08","12","13","14","15","17","23","24","25","26","27","28","29","30","31"];
    // A product's SAT keys: its own entry under "products" when there is one, else the branch default "product".
    static JsonObject SatKeys(JsonObject config,string productId)=>config.Obj("products")[productId] as JsonObject??config.Obj("product");
    public JsonObject BuildCfdi(BranchState s,JsonObject fiscal,JsonObject config)
    {
        var visit=s.Get("visit",fiscal.S("visitId"));Need(visit.S("status")=="closed","Ticket fiscal no liquidado");var units=s.Related("unit","visitId",visit.S("id")).Where(x=>x.S("status")=="delivered").OrderBy(x=>x.S("id"),StringComparer.Ordinal).ToArray();var weights=units.Select(x=>x.L("price")).Append(visit.L("deliveryFee")).ToArray();var amounts=Restaurant.Allocate(fiscal.Obj("totals").L("total"),weights);var items=new JsonArray();
        for(int i=0;i<weights.Length;i++){
            if(weights[i]==0)continue;var unit=i<units.Length?units[i]:null;var map=unit is null?config.Obj("delivery"):SatKeys(config,unit.S("productId"));var code=Text(map,"productCode",8);Need(Regex.IsMatch(code,@"^\d{8}$"),"Clave SAT inválida");var unitCode=Text(map,"unitCode",3);var taxObject=Choice(map,"taxObject","01","02","03");var bps=unit?.L("taxBps")??0;
            // A key that names a rate must match the rate frozen in the sold unit; the default follows each unit's own rate.
            if(map.ContainsKey("rateBps"))Need(map.L("rateBps")==bps,"Tasa fiscal distinta del precio histórico");Need(taxObject=="02"||bps==0,"Tratamiento fiscal incompatible");var tax=(long)Math.Round(amounts[i]*(decimal)bps/(10000+bps),MidpointRounding.AwayFromZero);var subtotal=amounts[i]-tax;var item=J.O("ProductCode",code,"IdentificationNumber",unit?.S("id")??"delivery","Description",unit?.S("name")??"Servicio de entrega","UnitCode",unitCode,"Quantity",1,"UnitPrice",subtotal/100m,"Subtotal",subtotal/100m,"TaxObject",taxObject,"Total",amounts[i]/100m);
            if(taxObject=="02")item["Taxes"]=J.N(new[]{J.O("Name","IVA","Rate",bps/10000m,"Base",subtotal/100m,"Total",tax/100m,"IsRetention",false)});items.Add(item);
        }
        Need(items.Count>0&&items.Objects().Sum(x=>x["Total"]!.GetValue<decimal>())==fiscal.Obj("totals").L("total")/100m,"Total CFDI inconsistente");var receiver=fiscal.Obj("receiver");var postal=Text(config,"expeditionPlace",5);Need(Regex.IsMatch(postal,@"^\d{5}$"),"Código postal emisor inválido");
        // PUE with several forms of payment: the SAT asks for the one that covered the largest amount.
        var main=s.Related("payment","visitId",visit.S("id")).GroupBy(x=>x.S("method")).Select(g=>(Method:g.Key,Amount:g.Sum(x=>x.L("amount")))).OrderByDescending(x=>x.Amount).ThenBy(x=>x.Method,StringComparer.Ordinal).FirstOrDefault();
        var form=config.S("paymentForm")!=""?config.S("paymentForm"):main.Method is null?"":config.Obj("paymentForms").S(main.Method) is {Length:>0} configured?configured:PaymentForms.GetValueOrDefault(main.Method,"");
        Need(SatPaymentForms.Contains(form),"Forma de pago "+(main.Method??"sin cobro")+" sin clave SAT; configúrala en paymentForms");
        return J.O("NameId","1","Currency","MXN","CfdiType","I","PaymentMethod","PUE","PaymentForm",form,"ExpeditionPlace",postal,"Exportation","01","Folio",fiscal.S("folio"),"Date",fiscal.S("issuedLocal"),"Receiver",J.O("Rfc",receiver.S("rfc"),"Name",receiver.S("name"),"TaxZipCode",receiver.S("postalCode"),"FiscalRegime",receiver.S("regime"),"CfdiUse",receiver.S("use")),"Items",items);
    }
    // Sandbox only: production stays off until there is a CSD and a reviewed contract. Requests marked sandboxAuthorized are
    // processed, or every request with allRequests (the configured fiscal service). Sandbox UUIDs are labelled as such.
    // A CFDI is never stamped twice: once a submission may have reached the PAC without its id coming back, only a
    // manager's fiscal.retry sends it again, with the same Folio and Date.
    public async Task<bool> ProcessFiscalSandbox(IPacAdapter pac,JsonObject config,bool allRequests=false,CancellationToken ct=default)
    {
        Need(pac.IsSandbox,"La automatización fiscal real permanece desactivada",403);var s=await Load(ct);
        // Only with the PAC live: with the simulator on the simulator stamps, and switched off nothing is sent.
        if(!Domain.IsLive(s,"pac"))return false;
        var configHash=J.Hash(J.Canonical(config));var now=DateTimeOffset.UtcNow;bool Leased(JsonObject x)=>DateTimeOffset.TryParse(x.S("leaseUntil"),out var lease)&&lease>=now;
        var fiscal=s.List("fiscal").Where(x=>(allRequests||x.B("sandboxAuthorized"))&&!Leased(x)).OrderBy(x=>x.S("at"),StringComparer.Ordinal).FirstOrDefault(x=>x.S("status") switch{
            "pending" or "submitting" or "sandbox-cancellation-pending"=>true,
            // Blocked until the configuration that blocked it changes, without holding back the requests behind it.
            "validation-blocked"=>x.S("configHash")!=configHash,
            "pending-reconciliation"=>x.S("providerId")!="",
            _=>false});
        if(fiscal is null)return false;var id=fiscal.S("id");
        // A claim that expired without the provider's id: the CFDI may have been stamped and the answer lost.
        if(fiscal.S("status")=="submitting"&&fiscal.S("providerId")==""){fiscal["status"]="pending-reconciliation";fiscal["message"]=Unanswered(fiscal);await Commit(s,J.Id(),"sandbox-fiscal-service",configHash,J.O("id",id,"status","pending-reconciliation"),"fiscal.sandbox.uncertain",ct);return true;}
        if(!fiscal.ContainsKey("pacPayload")&&fiscal.S("status")!="sandbox-cancellation-pending"){
            fiscal["status"]="validating";fiscal["environment"]="sandbox";await Commit(s,J.Id(),"sandbox-fiscal-service",configHash,J.O("id",id,"status","validating"),"fiscal.sandbox.validate",ct);s=await Load(ct);fiscal=s.Get("fiscal",id);
            try{fiscal["pacPayload"]=BuildCfdi(s,fiscal,config);fiscal["status"]="validated";}catch(BusinessException e){fiscal["status"]="validation-blocked";fiscal["message"]=e.Message;fiscal["configHash"]=configHash;await Commit(s,J.Id(),"sandbox-fiscal-service",configHash,J.O("id",id,"status","validation-blocked"),"fiscal.sandbox.validation.blocked",ct);return true;}
        }
        var cancel=fiscal.S("status")=="sandbox-cancellation-pending";fiscal["status"]=cancel?"sandbox-cancellation-pending":"submitting";fiscal["leaseUntil"]=DateTimeOffset.UtcNow.AddMinutes(5).ToString("O");var fingerprint=J.Hash(J.Canonical(fiscal));try{await Commit(s,J.Id(),"sandbox-fiscal-service",fingerprint,J.O("id",id,"status",fiscal.S("status")),"fiscal.sandbox.claim",ct);}catch(StoreException e)when(e.Code=="COS-DATA-CONFLICT"){return false;}
        PacResult result;try{result=cancel?await pac.Cancel(fiscal.S("providerId"),fiscal.S("sandboxUuid"),fiscal.S("cancelMotive"),fiscal.S("replacementUuid"),ct):fiscal.S("providerId")!=""?await pac.Consult(fiscal.S("providerId"),ct):await pac.Stamp(fiscal.Obj("pacPayload"),ct);}catch(Exception)when(!ct.IsCancellationRequested){result=new(cancel?"cancellation-pending":"pending-reconciliation",Message:"Respuesta sandbox incierta; conservar Folio y Date");}
        for(int attempt=0;attempt<8;attempt++){s=await Load(ct);var current=s.Get("fiscal",id);current["status"]=result.Status is "stamped" or "cancelled" or "cancellation-pending"?"sandbox-"+result.Status:result.Status;current["message"]="SANDBOX · sin validez fiscal. "+result.Message;current["leaseUntil"]=DateTimeOffset.UtcNow.AddSeconds(result.Status.Contains("pending")?30:0).ToString("O");if(result.ProviderId!="")current["providerId"]=result.ProviderId;if(result.FiscalUuid!="")current["sandboxUuid"]=result.FiscalUuid;current["providerAt"]=J.Now();
            if(current.S("status")=="pending-reconciliation"&&current.S("providerId")=="")current["message"]=Unanswered(current);
            try{await Commit(s,J.Id(),"sandbox-fiscal-service",fingerprint,J.O("id",id,"status",current.S("status")),"fiscal.sandbox.result",ct);return true;}catch(StoreException e)when(e.Code=="COS-DATA-CONFLICT"&&attempt<7){}}
        return false;
    }
    static string Unanswered(JsonObject fiscal)=>"SANDBOX · el PAC no respondió. Revisa en Facturama si existe el folio "+fiscal.S("folio")+": si no está, reintenta (mismo folio y fecha); si está, no reintentes.";
    public string FiscalState{get;private set;}="";
    public void NoteFiscal(string state)=>FiscalState=state;
}
