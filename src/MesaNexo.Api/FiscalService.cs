using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MesaNexo.Core;
using MesaNexo.Integrations;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
// The branch's PAC, from /etc/culinaryos/fiscal.json, readable only by root and culinaryos-app because it holds the PAC
// password (example in deploy/fiscal.example.json). Only Facturama's sandbox: stamping with fiscal validity needs the
// issuer's CSD, a contract with the PAC and a review of the SAT keys, and stays refused here until then.
public sealed record FiscalOptions(string Username,string Password,JsonObject Issuer)
{
    public static FiscalOptions Load(string path)=>From(JsonNode.Parse(File.ReadAllText(path)) as JsonObject??throw new BusinessException(400,"Configuración fiscal inválida"));
    public static FiscalOptions From(JsonObject o)
    {
        Need(o.S("provider")=="facturama","Proveedor PAC no soportado: "+o.S("provider"));
        Need(o.S("environment")=="sandbox","La facturación con validez fiscal sigue desactivada: requiere el CSD del emisor, contrato con el PAC y revisión de claves SAT. Usa \"environment\": \"sandbox\"",403);
        var issuer=o.Obj("issuer");Need(Regex.IsMatch(issuer.S("expeditionPlace"),@"^\d{5}$"),"issuer.expeditionPlace: código postal de expedición de 5 dígitos");
        Need(issuer.Obj("product").Count>0,"issuer.product: clave SAT por defecto para los productos (por ejemplo 90101501, unidad E48, objeto 02)");
        return new(Text(o,"username",100),Text(o,"password",200),(JsonObject)issuer.DeepClone());
    }
    public IPacAdapter Adapter()=>new FacturamaAdapter(Username,Password,sandbox:true);
}
// Sends fiscal requests to the PAC sandbox as they appear, and their cancellations. A request the PAC may have stamped
// without answering waits for a manager (fiscal.retry); it is never sent twice by this service.
public sealed class FiscalWorker(BranchService branch,IPacAdapter pac,JsonObject issuer,ILogger<FiscalWorker> log):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();var reported="";
        while(!ct.IsCancellationRequested){
            var delay=2000;
            try{if(await branch.ProcessFiscalSandbox(pac,issuer,allRequests:true,ct:ct)){delay=0;reported="";}branch.NoteFiscal("sandbox");}
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(BusinessException e){delay=10000;if(e.Message!=reported)log.LogWarning("Facturación en espera: {Reason}",e.Message);reported=e.Message;branch.NoteFiscal("error");}
            catch(Exception){delay=10000;log.LogWarning("Facturación pendiente; se reintenta");branch.NoteFiscal("error");}
            if(delay>0)try{await Task.Delay(delay,ct);}catch(OperationCanceledException){break;}
        }
    }
}
