using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
public sealed partial class BranchService
{
    // Import a proposal only. This method cannot create sales, invoices or payments.
    // Acceptance is a separate authenticated manager operation through existing /api/action.
    public async Task<JsonObject[]> ReceiveCloudProposals(JsonObject envelope,string trustedPublicKey,CancellationToken ct=default)
    {
        var data=envelope.Obj("data");using var rsa=RSA.Create();rsa.ImportFromPem(await File.ReadAllTextAsync(trustedPublicKey,ct));Need(rsa.VerifyData(Encoding.UTF8.GetBytes(J.Canonical(data)),Convert.FromBase64String(envelope.S("signature")),HashAlgorithmName.SHA256,RSASignaturePadding.Pss),"Firma nube inválida",403);
        Need(data.S("format")=="culinary-cloud-v1"&&data.S("siteId")==siteId,"Entrega de otra sucursal",403);Need(DateTimeOffset.TryParse(data.S("expires"),out var expires)&&expires>DateTimeOffset.UtcNow&&expires<DateTimeOffset.UtcNow.AddMinutes(16),"Entrega vencida o vigencia excesiva",403);Need(data.A("commands").Count<=1,"Recibe una propuesta por transacción");var receipts=new List<JsonObject>();
        foreach(var command in data.A("commands").Objects()){
            var id=Operation(command.S("id"));var digest=J.Hash(J.Canonical(command));Need(command.S("tenantId")==data.S("tenantId")&&command.S("branchId")==data.S("branchId"),"Comando de otra empresa",403);Need(new[]{"order","invoice","table","aggregator"}.Contains(command.S("type")),"Tipo de propuesta no autorizado");
            for(int attempt=0;attempt<12;attempt++){
                var s=await Load(ct);Need(s.Meta.S("tenantId")==data.S("tenantId")&&s.Meta.S("branchId")==data.S("branchId")&&s.Meta.B("cloudOrdersEnabled"),"Canal empresa/sucursal no aprovisionado localmente",403);
                var prior=s.List("cloudproposal").FirstOrDefault(x=>x.S("id")==id);if(prior is not null){Need(prior.S("digest")==digest,"Propuesta repetida con otro contenido",409);if(prior.S("status")!="pending-review")receipts.Add((JsonObject)prior.Obj("ack").DeepClone());break;}
                var stored=s.Add("cloudproposal",J.O("id",id,"siteId",siteId,"tenantId",data.S("tenantId"),"branchId",data.S("branchId"),"digest",digest,"command",command,"status","pending-review","receivedAt",J.Now()));if(command.S("type")=="table")Domain.ImportTableRequest(s,stored);else if(command.S("type")=="aggregator")Domain.ImportAggregatorOrder(s,stored);
                try{await Commit(s,J.Hash("proposal:"+siteId+":"+id)[..32],"cloud-proposal-receiver",digest,J.O("storedProposal",id),"cloud.proposal.receive",ct);if(stored.S("status")!="pending-review")receipts.Add((JsonObject)stored.Obj("ack").DeepClone());break;}catch(StoreException e)when(e.Code=="COS-DATA-CONFLICT"&&attempt<11){await Task.Delay(10,ct);}
            }
        }return receipts.ToArray();
    }
}
