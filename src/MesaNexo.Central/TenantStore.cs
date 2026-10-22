using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Central;
public readonly record struct TenantScope(string TenantId,string BranchId);
public sealed partial class CentralStore
{
    void InitializeTenants(SqliteConnection db)
    {
        using(var inspect=Command(db,null,"SELECT name FROM sqlite_master WHERE type='table' AND name='tenant'"))if(inspect.ExecuteScalar() is null){using var existing=Command(db,null,"SELECT 1 FROM provider LIMIT 1");if(existing.ExecuteScalar() is not null)Snapshot(database+".before-devmap-"+J.Id()+".sqlite");}
        using var migration=db.BeginTransaction();using var cmd=Command(db,migration,"""
        CREATE TABLE IF NOT EXISTS tenant(id TEXT PRIMARY KEY,slug TEXT UNIQUE NOT NULL,name TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS tenant_branch(tenant_id TEXT NOT NULL,id TEXT NOT NULL,site_id TEXT UNIQUE NOT NULL,certificate TEXT UNIQUE NOT NULL,slug TEXT UNIQUE NOT NULL,name TEXT NOT NULL,PRIMARY KEY(tenant_id,id),FOREIGN KEY(tenant_id) REFERENCES tenant(id));
        CREATE TABLE IF NOT EXISTS tenant_user(tenant_id TEXT NOT NULL,id TEXT NOT NULL,username TEXT NOT NULL,value BLOB NOT NULL,PRIMARY KEY(tenant_id,id),UNIQUE(tenant_id,username),FOREIGN KEY(tenant_id) REFERENCES tenant(id));
        CREATE TABLE IF NOT EXISTS membership(tenant_id TEXT NOT NULL,user_id TEXT NOT NULL,branch_id TEXT NOT NULL,PRIMARY KEY(tenant_id,user_id,branch_id),FOREIGN KEY(tenant_id,user_id) REFERENCES tenant_user(tenant_id,id),FOREIGN KEY(tenant_id,branch_id) REFERENCES tenant_branch(tenant_id,id));
        CREATE TABLE IF NOT EXISTS tenant_session(token_hash TEXT PRIMARY KEY,tenant_id TEXT NOT NULL,user_id TEXT NOT NULL,expires TEXT NOT NULL,device_id TEXT NOT NULL,FOREIGN KEY(tenant_id,user_id) REFERENCES tenant_user(tenant_id,id));
        CREATE TABLE IF NOT EXISTS mirror(tenant_id TEXT NOT NULL,branch_id TEXT NOT NULL,kind TEXT NOT NULL,id TEXT NOT NULL,version INTEGER NOT NULL,value BLOB NOT NULL,source_at TEXT NOT NULL,received_at TEXT NOT NULL,PRIMARY KEY(tenant_id,branch_id,kind,id),FOREIGN KEY(tenant_id,branch_id) REFERENCES tenant_branch(tenant_id,id));
        CREATE TABLE IF NOT EXISTS cloud_order(tenant_id TEXT NOT NULL,branch_id TEXT NOT NULL,id TEXT NOT NULL,request_id TEXT NOT NULL,fingerprint TEXT NOT NULL,access_hash TEXT NOT NULL,status TEXT NOT NULL,value BLOB NOT NULL,created_at TEXT NOT NULL,PRIMARY KEY(tenant_id,branch_id,id),UNIQUE(tenant_id,branch_id,request_id),FOREIGN KEY(tenant_id,branch_id) REFERENCES tenant_branch(tenant_id,id));
        CREATE TABLE IF NOT EXISTS cloud_command(tenant_id TEXT NOT NULL,branch_id TEXT NOT NULL,id TEXT NOT NULL,type TEXT NOT NULL,status TEXT NOT NULL,digest TEXT NOT NULL,value BLOB NOT NULL,receipt BLOB,PRIMARY KEY(tenant_id,branch_id,id),FOREIGN KEY(tenant_id,branch_id) REFERENCES tenant_branch(tenant_id,id));
        CREATE TABLE IF NOT EXISTS webhook_receipt(tenant_id TEXT NOT NULL,branch_id TEXT NOT NULL,provider TEXT NOT NULL,id TEXT NOT NULL,digest TEXT NOT NULL,value BLOB NOT NULL,PRIMARY KEY(tenant_id,branch_id,provider,id),FOREIGN KEY(tenant_id,branch_id) REFERENCES tenant_branch(tenant_id,id));
        CREATE TABLE IF NOT EXISTS invoice_capability(token_hash TEXT PRIMARY KEY,tenant_id TEXT NOT NULL,branch_id TEXT NOT NULL,visit_id TEXT NOT NULL,expires TEXT NOT NULL,used INTEGER NOT NULL DEFAULT 0,attempts INTEGER NOT NULL DEFAULT 0,FOREIGN KEY(tenant_id,branch_id) REFERENCES tenant_branch(tenant_id,id));
        CREATE TABLE IF NOT EXISTS public_attempt(token_hash TEXT PRIMARY KEY,attempts INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS mirror_kind ON mirror(tenant_id,branch_id,kind,received_at);
        CREATE INDEX IF NOT EXISTS command_pending ON cloud_command(tenant_id,branch_id,status);
        CREATE INDEX IF NOT EXISTS order_created ON cloud_order(tenant_id,branch_id,created_at);
        """);cmd.ExecuteNonQuery();
        using(var info=Command(db,migration,"PRAGMA table_info(tenant_session)")){using var reader=info.ExecuteReader();var hasDevice=false;while(reader.Read())hasDevice|=reader.GetString(1)=="device_id";reader.Close();if(!hasDevice){using var alter=Command(db,migration,"ALTER TABLE tenant_session ADD COLUMN device_id TEXT NOT NULL DEFAULT '';DELETE FROM tenant_session;");alter.ExecuteNonQuery();}}
        using(var version=Command(db,migration,"PRAGMA user_version=2"))version.ExecuteNonQuery();migration.Commit();
    }
    string Context(TenantScope s,string kind,string id)=>"tenant-v1:"+s.TenantId+":"+s.BranchId+":"+kind+":"+id;
    public void ProvisionTenant(JsonObject p,IPasswordHasher hasher)
    {
        var tenant=Text(p,"tenantId",64);var branch=Text(p,"branchId",64);var slug=Text(p,"slug",100);Need(System.Text.RegularExpressions.Regex.IsMatch(slug,"^[a-z0-9-]{1,100}$"),"Slug inválido");var fp=Text(p,"certificateSha256",64).ToLowerInvariant();Need(System.Text.RegularExpressions.Regex.IsMatch(fp,"^[0-9a-f]{64}$"),"Huella de certificado inválida");
        lock(gate){using var db=Open();using var tx=db.BeginTransaction();using var t=Command(db,tx,"INSERT INTO tenant VALUES($t,$s,$n) ON CONFLICT(id) DO NOTHING",("$t",tenant),("$s",p.S("tenantSlug",tenant)),("$n",Text(p,"tenantName",150)));t.ExecuteNonQuery();
            using var b=Command(db,tx,"INSERT INTO tenant_branch VALUES($t,$b,$site,$fp,$slug,$n)",("$t",tenant),("$b",branch),("$site",Text(p,"siteId",32)),("$fp",fp),("$slug",slug),("$n",Text(p,"branchName",150)));b.ExecuteNonQuery();
            var user=J.O("id",J.Id(),"name",Text(p,"ownerName",100),"username",Text(p,"ownerUsername",100),"role","dueno","active",true,"password",hasher.Hash(p.S("ownerPassword")),"enrollmentHash",J.Hash(Text(p,"ownerEnrollmentCode",200)),"devices",new JsonArray());using var existing=Command(db,tx,"SELECT id FROM tenant_user WHERE tenant_id=$t AND username=$u",("$t",tenant),("$u",user.S("username")));var uid=existing.ExecuteScalar() as string;
            if(uid is null){uid=user.S("id");using var u=Command(db,tx,"INSERT INTO tenant_user VALUES($t,$id,$u,$v)",("$t",tenant),("$id",uid),("$u",user.S("username")),("$v",Seal("tenant-user:"+tenant+":"+uid,user.ToJsonString())));u.ExecuteNonQuery();}
            using var m=Command(db,tx,"INSERT INTO membership VALUES($t,$u,$b)",("$t",tenant),("$u",uid),("$b",branch));m.ExecuteNonQuery();tx.Commit();}
    }
    public JsonObject TenantLogin(JsonObject p,IPasswordHasher hasher)
    {
        lock(gate){using var db=Open();using var tx=db.BeginTransaction();using var cmd=Command(db,tx,"SELECT id,value FROM tenant_user WHERE tenant_id=$t AND username=$u",("$t",p.S("tenantId")),("$u",p.S("username")));string id;JsonObject user;using(var reader=cmd.ExecuteReader()){Need(reader.Read(),"Credenciales incorrectas",401);id=reader.GetString(0);user=JsonNode.Parse(Unseal("tenant-user:"+p.S("tenantId")+":"+id,(byte[])reader[1]))!.AsObject();}
            Need(user.B("active")&&hasher.Verify(user.S("password"),p.S("password")),"Credenciales incorrectas",401);using var permission=Command(db,tx,"SELECT 1 FROM membership WHERE tenant_id=$t AND branch_id=$b AND user_id=$u",("$t",p.S("tenantId")),("$b",p.S("branchId")),("$u",id));Need(permission.ExecuteScalar() is not null,"Sucursal no autorizada",403);
            Need(Guid.TryParse(p.S("deviceId"),out _)&&p.S("deviceSecret").Length>=32,"Dispositivo requerido");var devices=user.A("devices");var device=devices.Objects().FirstOrDefault(x=>x.S("id")==p.S("deviceId"));
            if(device is null){Need(p.S("enrollmentCode").Length>=20&&J.Hash(p.S("enrollmentCode"))==user.S("enrollmentHash"),"Alta de dispositivo no autorizada",403);device=J.O("id",p.S("deviceId"),"secretHash",J.Hash(p.S("deviceSecret")),"authorized",true);devices.Add(device);user["enrollmentHash"]="";using var save=Command(db,tx,"UPDATE tenant_user SET value=$v WHERE tenant_id=$t AND id=$u",("$v",Seal("tenant-user:"+p.S("tenantId")+":"+id,user.ToJsonString())),("$t",p.S("tenantId")),("$u",id));save.ExecuteNonQuery();}Need(device.B("authorized")&&device.S("secretHash")==J.Hash(p.S("deviceSecret")),"Dispositivo revocado o inválido",403);
            var token="tenant."+Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));using var add=Command(db,tx,"INSERT INTO tenant_session VALUES($h,$t,$u,$e,$device)",("$h",J.Hash(token)),("$t",p.S("tenantId")),("$u",id),("$e",DateTimeOffset.UtcNow.AddHours(12).ToString("O")),("$device",p.S("deviceId")));add.ExecuteNonQuery();tx.Commit();user.Remove("password");user.Remove("devices");user.Remove("enrollmentHash");return J.O("token",token,"user",user,"tenantId",p.S("tenantId"),"branchId",p.S("branchId"));}
    }
    public TenantScope AuthorizeTenant(string token,string branch)
    {
        using var db=Open();using var cmd=Command(db,null,"SELECT s.tenant_id,s.user_id,s.device_id,u.value FROM tenant_session s JOIN membership m ON m.tenant_id=s.tenant_id AND m.user_id=s.user_id JOIN tenant_user u ON u.tenant_id=s.tenant_id AND u.id=s.user_id WHERE s.token_hash=$h AND s.expires>$now AND m.branch_id=$b",("$h",J.Hash(token)),("$now",J.Now()),("$b",branch));using var reader=cmd.ExecuteReader();Need(reader.Read(),"Empresa/sucursal no autorizada",403);var tenant=reader.GetString(0);var user=JsonNode.Parse(Unseal("tenant-user:"+tenant+":"+reader.GetString(1),(byte[])reader[3]))!.AsObject();Need(user.B("active")&&user.A("devices").Objects().Any(d=>d.S("id")==reader.GetString(2)&&d.B("authorized")),"Usuario o dispositivo revocado",403);return new(tenant,branch);
    }
    public void TenantLogout(string token){using var db=Open();using var cmd=Command(db,null,"DELETE FROM tenant_session WHERE token_hash=$h",("$h",J.Hash(token)));cmd.ExecuteNonQuery();}
    public TenantScope CertificateScope(string fingerprint)
    {
        using var db=Open();using var cmd=Command(db,null,"SELECT tenant_id,id FROM tenant_branch WHERE certificate=$fp",("$fp",fingerprint.ToLowerInvariant()));using var r=cmd.ExecuteReader();Need(r.Read(),"Certificado sin empresa y sucursal autorizadas",403);return new(r.GetString(0),r.GetString(1));
    }
    public TenantScope PublicScope(string slug)
    {
        using var db=Open();using var cmd=Command(db,null,"SELECT tenant_id,id FROM tenant_branch WHERE slug=$s",("$s",slug));using var r=cmd.ExecuteReader();Need(r.Read(),"Tienda no encontrada",404);return new(r.GetString(0),r.GetString(1));
    }
    public JsonObject[] Mirror(TenantScope s,string kind)
    {
        using var db=Open();using var cmd=Command(db,null,"SELECT id,value,source_at,received_at FROM mirror WHERE tenant_id=$t AND branch_id=$b AND kind=$k",("$t",s.TenantId),("$b",s.BranchId),("$k",kind));using var r=cmd.ExecuteReader();var rows=new List<JsonObject>();while(r.Read()){var d=JsonNode.Parse(Unseal(Context(s,kind,r.GetString(0)),(byte[])r[1]))!.AsObject();d["sourceAt"]=r.GetString(2);d["receivedAt"]=r.GetString(3);rows.Add(d);}return rows.ToArray();
    }
    void ProjectTenantEvent(SqliteConnection db,SqliteTransaction tx,string site,JsonNode wire)
    {
        using var scope=Command(db,tx,"SELECT tenant_id,id FROM tenant_branch WHERE site_id=$s",("$s",site));TenantScope s;using(var reader=scope.ExecuteReader()){if(!reader.Read())return;s=new(reader.GetString(0),reader.GetString(1));}
        foreach(var c in wire.Obj("data").A("changes").Objects()){
            var d=(JsonObject)c.Obj("data").DeepClone();var kind=d.S("kind");if(!new[]{"meta","table","product","category","promotion","zone","visit","unit","payment","cash","request","notification","invoicetoken","fiscal","ownernotice"}.Contains(kind))continue;
            Need(d.S("siteId")==site,"Proyección de otra sucursal",403);d.Remove("trackingKey");d.Remove("enrollmentHash");d.Remove("qrToken");var id=d.S("id");using var cmd=Command(db,tx,"INSERT INTO mirror VALUES($t,$b,$k,$id,$version,$v,$source,$now) ON CONFLICT(tenant_id,branch_id,kind,id) DO UPDATE SET version=excluded.version,value=excluded.value,source_at=excluded.source_at,received_at=excluded.received_at WHERE excluded.version>mirror.version",("$t",s.TenantId),("$b",s.BranchId),("$k",kind),("$id",id),("$version",c.L("expected_version")+1),("$v",Seal(Context(s,kind,id),d.ToJsonString())),("$source",wire.Obj("data").Obj("receipt").S("at",J.Now())),("$now",J.Now()));cmd.ExecuteNonQuery();
            if(kind=="invoicetoken"){using var cap=Command(db,tx,"INSERT INTO invoice_capability(token_hash,tenant_id,branch_id,visit_id,expires) VALUES($h,$t,$b,$v,$e) ON CONFLICT(token_hash) DO NOTHING",("$h",d.S("tokenHash")),("$t",s.TenantId),("$b",s.BranchId),("$v",d.S("visitId")),("$e",d.S("expires")));cap.ExecuteNonQuery();}
        }
    }
    public JsonObject StoreOrder(TenantScope s,JsonObject payload,string type="order")
    {
        Need(Guid.TryParse(payload.S("requestId"),out _),"Solicitud UUID requerida");Need(System.Text.RegularExpressions.Regex.IsMatch(payload.S("clientToken"),"^[0-9a-f]{64}$"),"Token de seguimiento requerido");var fingerprint=J.Hash(J.Canonical(payload));
        lock(gate){using var db=Open();using var tx=db.BeginTransaction();using var prior=Command(db,tx,"SELECT id,fingerprint,status FROM cloud_order WHERE tenant_id=$t AND branch_id=$b AND request_id=$r",("$t",s.TenantId),("$b",s.BranchId),("$r",payload.S("requestId")));using(var r=prior.ExecuteReader()){if(r.Read()){Need(r.GetString(1)==fingerprint,"Solicitud reutilizada con otro contenido",409);return J.O("id",r.GetString(0),"status",r.GetString(2),"duplicate",true);}}
            var id=J.Id();var data=(JsonObject)payload.DeepClone();data.Remove("clientToken");var status=data.S("paymentMethod")=="online"?"requires-verified-payment":"pending-branch";
            using var insert=Command(db,tx,"INSERT INTO cloud_order VALUES($t,$b,$id,$r,$f,$a,$status,$v,$now)",("$t",s.TenantId),("$b",s.BranchId),("$id",id),("$r",payload.S("requestId")),("$f",fingerprint),("$a",J.Hash(payload.S("clientToken"))),("$status",status),("$v",Seal(Context(s,"order",id),data.ToJsonString())),("$now",J.Now()));insert.ExecuteNonQuery();if(status=="pending-branch")QueueCommand(db,tx,s,id,type,data);tx.Commit();return J.O("id",id,"status",status,"acceptedLocally",false);}
    }
    void QueueCommand(SqliteConnection db,SqliteTransaction tx,TenantScope s,string id,string type,JsonObject payload)
    {
        var data=J.O("tenantId",s.TenantId,"branchId",s.BranchId,"id",id,"type",type,"payload",payload,"at",J.Now());using var cmd=Command(db,tx,"INSERT INTO cloud_command VALUES($t,$b,$id,$type,'pending',$d,$v,NULL)",("$t",s.TenantId),("$b",s.BranchId),("$id",id),("$type",type),("$d",J.Hash(J.Canonical(data))),("$v",Seal(Context(s,"command",id),data.ToJsonString())));cmd.ExecuteNonQuery();
    }
    public JsonObject[] PendingCommands(TenantScope s)
    {
        using var db=Open();using var cmd=Command(db,null,"SELECT id,value FROM cloud_command WHERE tenant_id=$t AND branch_id=$b AND status='pending' ORDER BY id LIMIT 1",("$t",s.TenantId),("$b",s.BranchId));using var r=cmd.ExecuteReader();var result=new List<JsonObject>();while(r.Read())result.Add(JsonNode.Parse(Unseal(Context(s,"command",r.GetString(0)),(byte[])r[1]))!.AsObject());return result.ToArray();
    }
    public JsonObject CommandAck(TenantScope s,string id,JsonObject ack)
    {
        lock(gate){using var db=Open();using var tx=db.BeginTransaction();using var read=Command(db,tx,"SELECT status,digest,receipt,type FROM cloud_command WHERE tenant_id=$t AND branch_id=$b AND id=$id",("$t",s.TenantId),("$b",s.BranchId),("$id",id));string type;using(var r=read.ExecuteReader()){Need(r.Read(),"Pedido de otra sucursal",404);Need(r.GetString(1)==ack.S("digest"),"Acuse de otro contenido",409);type=r.GetString(3);if(r.GetString(0)!="pending"){Need(J.Canonical(JsonNode.Parse(Unseal(Context(s,"ack",id),(byte[])r[2])))==J.Canonical(ack),"Acuse contradictorio",409);return J.O("committed",true,"duplicate",true);}}
            Need(new[]{"accepted-local","rejected-local"}.Contains(ack.S("status")),"Acuse local inválido");using var update=Command(db,tx,"UPDATE cloud_command SET status=$status,receipt=$v WHERE tenant_id=$t AND branch_id=$b AND id=$id",("$status",ack.S("status")),("$v",Seal(Context(s,"ack",id),ack.ToJsonString())),("$t",s.TenantId),("$b",s.BranchId),("$id",id));update.ExecuteNonQuery();if(type is "order" or "table"){using var order=Command(db,tx,"UPDATE cloud_order SET status=$status WHERE tenant_id=$t AND branch_id=$b AND id=$id",("$status",ack.S("status")),("$t",s.TenantId),("$b",s.BranchId),("$id",id));order.ExecuteNonQuery();}tx.Commit();return J.O("committed",true);}
    }
    public JsonObject OrderStatus(TenantScope s,string id,string access)
    {
        using var db=Open();using var cmd=Command(db,null,"SELECT status FROM cloud_order WHERE tenant_id=$t AND branch_id=$b AND id=$id AND access_hash=$a",("$t",s.TenantId),("$b",s.BranchId),("$id",id),("$a",J.Hash(access)));var status=cmd.ExecuteScalar() as string;Need(status is not null,"Pedido no autorizado",404);return J.O("id",id,"status",status,"acceptedLocally",status=="accepted-local");
    }
}
