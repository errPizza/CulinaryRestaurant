using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Central;

public sealed partial class CentralStore {
    public JsonObject SubmitInvoice(string token, JsonObject receiver) {

        Need(token.Length == 64, "Token inválido", 403);
        var hash = J.Hash(token);
        TenantScope scope;
        string visit;
        lock (gate) {

            using var db = Open();
            using (var tx = db.BeginTransaction()) {

                using var read = Command(db, tx, "SELECT tenant_id,branch_id,visit_id,used,attempts,expires FROM invoice_capability WHERE token_hash=@h", ("@h", hash));
                using (var r = read.ExecuteReader()) { Need(r.Read(), "Token inválido o todavía no sincronizado", 403); Need(r.GetInt32(3) == 0 && r.GetInt32(4) < 10 && DateTimeOffset.Parse(r.GetString(5)) > DateTimeOffset.UtcNow, "Token usado, vencido o bloqueado", 403); scope = new(r.GetString(0), r.GetString(1)); visit = r.GetString(2); }
                using var attempt = Command(db, tx, "UPDATE invoice_capability SET attempts=attempts+1 WHERE token_hash=@h", ("@h", hash));
                attempt.ExecuteNonQuery();
                tx.Commit();

            }
            var validated = Restaurant.FiscalReceiver(receiver);
            using var commit = db.BeginTransaction();
            using var consume = Command(db, commit, "UPDATE invoice_capability SET used=1 WHERE token_hash=@h AND used=0 AND expires>@now", ("@h", hash), ("@now", J.Now()));
            Need(consume.ExecuteNonQuery() == 1, "Token ya utilizado", 409);
            var id = J.Id();
            QueueCommand(db, commit, scope, id, "invoice", J.O("visitId", visit, "receiver", validated));
            commit.Commit();
            return J.O("id", id, "status", "pending-branch", "message", "Solicitud recibida; todavía no es un CFDI timbrado");

        }

    }
}
