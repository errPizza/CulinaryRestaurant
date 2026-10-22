using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
public sealed partial class BranchService
{
    public JsonObject BuildCfdi(BranchState s,JsonObject fiscal,JsonObject config)
    {
        var visit=s.Get("visit",fiscal.S("visitId"));Need(visit.S("status")=="closed","Ticket fiscal no liquidado");var units=s.List("unit").Where(x=>x.S("visitId")==visit.S("id")&&x.S("status")=="delivered").OrderBy(x=>x.S("id"),StringComparer.Ordinal).ToArray();var weights=units.Select(x=>x.L("price")).Append(visit.L("deliveryFee")).ToArray();var amounts=Restaurant.Allocate(fiscal.Obj("totals").L("total"),weights);var items=new JsonArray();
        for(int i=0;i<weights.Length;i++){
            if(weights[i]==0)continue;var unit=i<units.Length?units[i]:null;var map=unit is null?config.Obj("delivery"):config.Obj("products").Obj(unit.S("productId"));var code=Text(map,"productCode",8);Need(System.Text.RegularExpressions.Regex.IsMatch(code,@"^\d{8}$"),"Clave SAT inválida");var unitCode=Text(map,"unitCode",3);var taxObject=Choice(map,"taxObject","01","02","03");var bps=unit?.L("taxBps")??0;Need(map.L("rateBps")==bps,"Tasa fiscal distinta del precio histórico");Need(taxObject=="02"||bps==0,"Tratamiento fiscal incompatible");var tax=(long)Math.Round(amounts[i]*(decimal)bps/(10000+bps),MidpointRounding.AwayFromZero);var subtotal=amounts[i]-tax;var item=J.O("ProductCode",code,"IdentificationNumber",unit?.S("id")??"delivery","Description",unit?.S("name")??"Servicio de entrega","UnitCode",unitCode,"Quantity",1,"UnitPrice",subtotal/100m,"Subtotal",subtotal/100m,"TaxObject",taxObject,"Total",amounts[i]/100m);
            if(taxObject=="02")item["Taxes"]=J.N(new[]{J.O("Name","IVA","Rate",bps/10000m,"Base",subtotal/100m,"Total",tax/100m,"IsRetention",false)});items.Add(item);
        }
        Need(items.Count>0&&items.Objects().Sum(x=>x["Total"]!.GetValue<decimal>())==fiscal.Obj("totals").L("total")/100m,"Total CFDI inconsistente");var receiver=fiscal.Obj("receiver");var postal=Text(config,"expeditionPlace",5);Need(System.Text.RegularExpressions.Regex.IsMatch(postal,@"^\d{5}$"),"Código postal emisor inválido");return J.O("NameId","1","Currency","MXN","CfdiType","I","PaymentMethod","PUE","PaymentForm",Choice(config,"paymentForm","01","02","03","04","28","99"),"ExpeditionPlace",postal,"Exportation","01","Folio",fiscal.S("folio"),"Date",fiscal.S("issuedLocal"),"Receiver",J.O("Rfc",receiver.S("rfc"),"Name",receiver.S("name"),"TaxZipCode",receiver.S("postalCode"),"FiscalRegime",receiver.S("regime"),"CfdiUse",receiver.S("use")),"Items",items);
    }
    // Explicit sandbox-only processing. No hosted worker, public route or production PAC activation.
    // A local manager must separately authorize each test request. Sandbox UUIDs are labelled as such.
    public async Task<bool> ProcessFiscalSandbox(IPacAdapter pac,JsonObject config,CancellationToken ct=default)
    {
        Need(pac.IsSandbox,"La automatización fiscal real permanece desactivada",403);var s=await Load(ct);var fiscal=s.List("fiscal").FirstOrDefault(x=>x.B("sandboxAuthorized")&&new[]{"pending","validation-blocked","submitting","pending-reconciliation","sandbox-cancellation-pending"}.Contains(x.S("status"))&&(!DateTimeOffset.TryParse(x.S("leaseUntil"),out var lease)||lease<DateTimeOffset.UtcNow));if(fiscal is null)return false;var id=fiscal.S("id");var configHash=J.Hash(J.Canonical(config));
        if(fiscal.S("status")=="validation-blocked"&&fiscal.S("configHash")==configHash)return false;
        if(!fiscal.ContainsKey("pacPayload")&&fiscal.S("status")!="sandbox-cancellation-pending"){
            fiscal["status"]="validating";fiscal["environment"]="sandbox";await Commit(s,J.Id(),"sandbox-fiscal-service",configHash,J.O("id",id,"status","validating"),"fiscal.sandbox.validate",ct);s=await Load(ct);fiscal=s.Get("fiscal",id);
            try{fiscal["pacPayload"]=BuildCfdi(s,fiscal,config);fiscal["status"]="validated";}catch(BusinessException e){fiscal["status"]="validation-blocked";fiscal["message"]=e.Message;fiscal["configHash"]=configHash;await Commit(s,J.Id(),"sandbox-fiscal-service",configHash,J.O("id",id,"status","validation-blocked"),"fiscal.sandbox.validation.blocked",ct);return false;}
        }
        var cancel=fiscal.S("status")=="sandbox-cancellation-pending";fiscal["status"]=cancel?"sandbox-cancellation-pending":"submitting";fiscal["leaseUntil"]=DateTimeOffset.UtcNow.AddMinutes(5).ToString("O");var fingerprint=J.Hash(J.Canonical(fiscal));try{await Commit(s,J.Id(),"sandbox-fiscal-service",fingerprint,J.O("id",id,"status",fiscal.S("status")),"fiscal.sandbox.claim",ct);}catch(StoreException e)when(e.Code=="COS-DATA-CONFLICT"){return false;}
        PacResult result;try{result=cancel?await pac.Cancel(fiscal.S("providerId"),fiscal.S("sandboxUuid"),fiscal.S("cancelMotive"),fiscal.S("replacementUuid"),ct):fiscal.S("providerId")!=""?await pac.Consult(fiscal.S("providerId"),ct):await pac.Stamp(fiscal.Obj("pacPayload"),ct);}catch(Exception)when(!ct.IsCancellationRequested){result=new(cancel?"cancellation-pending":"pending-reconciliation",Message:"Respuesta sandbox incierta; conservar Folio y Date");}
        for(int attempt=0;attempt<8;attempt++){s=await Load(ct);var current=s.Get("fiscal",id);current["status"]=result.Status is "stamped" or "cancelled" or "cancellation-pending"?"sandbox-"+result.Status:result.Status;current["message"]="SANDBOX · sin validez fiscal. "+result.Message;current["leaseUntil"]=DateTimeOffset.UtcNow.AddSeconds(result.Status.Contains("pending")?30:0).ToString("O");if(result.ProviderId!="")current["providerId"]=result.ProviderId;if(result.FiscalUuid!="")current["sandboxUuid"]=result.FiscalUuid;current["providerAt"]=J.Now();try{await Commit(s,J.Id(),"sandbox-fiscal-service",fingerprint,J.O("id",id,"status",current.S("status")),"fiscal.sandbox.result",ct);return true;}catch(StoreException e)when(e.Code=="COS-DATA-CONFLICT"&&attempt<7){}}
        return false;
    }
}
