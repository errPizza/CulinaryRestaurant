using System.Text.Json.Nodes;
using MesaNexo.Core;
using MesaNexo.Integrations;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Central;
public sealed partial class CentralStore
{
    // Binding requires the original order capability, before accepting any provider webhook.
    public void BindPaymentIntent(TenantScope scope,string id,string access,string intent)
    {
        Need(intent.StartsWith("pi_")&&intent.Length<200,"Intento inválido");lock(gate){using var db=Open();using var tx=db.BeginTransaction();using var read=Command(db,tx,"SELECT value FROM cloud_order WHERE tenant_id=$t AND branch_id=$b AND id=$id AND access_hash=$a AND status='requires-verified-payment'",("$t",scope.TenantId),("$b",scope.BranchId),("$id",id),("$a",J.Hash(access)));Need(read.ExecuteScalar() is byte[],"Pedido no autorizado",403);var body=JsonNode.Parse(Unseal(Context(scope,"order",id),(byte[])read.ExecuteScalar()!))!.AsObject();Need(body.S("paymentIntentId",intent)==intent,"Intento ya asignado",409);body["paymentIntentId"]=intent;using var update=Command(db,tx,"UPDATE cloud_order SET value=$v WHERE tenant_id=$t AND branch_id=$b AND id=$id",("$v",Seal(Context(scope,"order",id),body.ToJsonString())),("$t",scope.TenantId),("$b",scope.BranchId),("$id",id));update.ExecuteNonQuery();tx.Commit();}
    }
    public JsonObject ReceiveStripe(TenantScope scope,byte[] raw,string signature,string webhookSecret)
    {
        var e=SignedWebhooks.Stripe(raw,signature,webhookSecret);Need(e.S("type")=="payment_intent.succeeded","Evento no procesable");var obj=e.Obj("data").Obj("object");Need(obj.S("status")=="succeeded"&&obj.S("currency")=="mxn","Pago no confirmado");var metadata=obj.Obj("metadata");Need(metadata.S("tenant_id")==scope.TenantId&&metadata.S("branch_id")==scope.BranchId,"Pago de otra empresa/sucursal",403);var id=metadata.S("order_id");var eventId=Text(e,"id",200);var digest=J.Hash(System.Text.Encoding.UTF8.GetString(raw));
        lock(gate){using var db=Open();using var tx=db.BeginTransaction();using var prior=Command(db,tx,"SELECT digest,value FROM webhook_receipt WHERE tenant_id=$t AND branch_id=$b AND provider='stripe' AND id=$e",("$t",scope.TenantId),("$b",scope.BranchId),("$e",eventId));using(var r=prior.ExecuteReader()){if(r.Read()){Need(r.GetString(0)==digest,"Webhook repetido con otro contenido",409);return J.O("committed",true,"duplicate",true);}}
            using var read=Command(db,tx,"SELECT value,status FROM cloud_order WHERE tenant_id=$t AND branch_id=$b AND id=$id",("$t",scope.TenantId),("$b",scope.BranchId),("$id",id));JsonObject data;using(var r=read.ExecuteReader()){Need(r.Read()&&r.GetString(1)=="requires-verified-payment","Pedido no pendiente de pago",409);data=JsonNode.Parse(Unseal(Context(scope,"order",id),(byte[])r[0]))!.AsObject();}
            Need(data.S("paymentIntentId")!=""&&data.S("paymentIntentId")==obj.S("id")&&data.L("quotedTotal")==obj.L("amount_received"),"Intento o importe del proveedor no corresponde al pedido",403);data["verifiedPayment"]=J.O("provider","stripe","id",obj.S("id"),"eventId",eventId,"currency","mxn","amount",obj.L("amount_received"),"at",J.Now());using var update=Command(db,tx,"UPDATE cloud_order SET status='payment-verified-pending-branch',value=$v WHERE tenant_id=$t AND branch_id=$b AND id=$id",("$v",Seal(Context(scope,"order",id),data.ToJsonString())),("$t",scope.TenantId),("$b",scope.BranchId),("$id",id));update.ExecuteNonQuery();QueueCommand(db,tx,scope,id,"order",data);using var receipt=Command(db,tx,"INSERT INTO webhook_receipt VALUES($t,$b,'stripe',$e,$d,$v)",("$t",scope.TenantId),("$b",scope.BranchId),("$e",eventId),("$d",digest),("$v",Seal(Context(scope,"stripe-event",eventId),e.ToJsonString())));receipt.ExecuteNonQuery();tx.Commit();return J.O("committed",true,"status","payment-verified-pending-branch");}
    }
}
