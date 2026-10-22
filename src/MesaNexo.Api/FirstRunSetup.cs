using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
// Setup mode: the person at the installed equipment creates the restaurant and its manager from the app.
// Only an empty store and the one-time code written by the CulinaryOS first boot can be initialized.
public sealed partial class BranchService
{
    public async Task<bool> AwaitingSetup(CancellationToken ct=default)
    {
        var h=await store.CallAsync(J.O("op","health"),ct);if(h?.B("ready")!=true||h.S("site_id")!=siteId)return false;
        var c=await store.CallAsync(J.O("op","capabilities_v1"),ct);if(!c!.A("extensions").Strings().Contains("transaction_v1"))return false;
        return await store.CallAsync(J.O("op","get","record_id","mn_meta_site"),ct) is null;
    }
    public async Task<JsonObject> Setup(JsonObject p,string? setupCode,CancellationToken ct=default)
    {
        Need(setupCode is {Length:>=32},"El modo Setup no está disponible en este equipo",404);
        Need(p.S("siteId")==siteId,"Sucursal equivocada",403);
        Need(CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(p.S("setupCode"))),SHA256.HashData(Encoding.UTF8.GetBytes(setupCode!))),"Código de configuración no válido",403);
        Need(p.S("managerPassword")==p.S("managerPasswordConfirm"),"Las contraseñas no coinciden");
        // The device code is generated here and returned once, so the first device can be authorized.
        var enrollment=Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+','-').Replace('/','_');var username=Text(p,"managerUsername",100);
        await Initialize(J.O("siteId",siteId,"name",Text(p,"name",150),"mode","licencia","managerName",Text(p,"managerName",100),"managerUsername",username,"managerPassword",p.S("managerPassword"),"enrollmentCode",enrollment,"modules",new JsonArray(),"maxUsers",15,"maxTables",30),ct);
        return J.O("initialized",true,"siteId",siteId,"username",username,"enrollmentCode",enrollment);
    }
}
