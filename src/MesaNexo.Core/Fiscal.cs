using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;

public sealed partial class Restaurant {
    // Requests in these states no longer block a new request or a reopen.
    static readonly string[] FiscalClosed = ["rejected", "cancelled", "sandbox-cancelled", "simulated-rejected", "simulated-cancelled"];
    public static JsonObject FiscalReceiver(JsonObject p) {

        var rfc = Text(p, "rfc", 13).ToUpperInvariant();
        var zip = Text(p, "postalCode", 5);
        Need(Regex.IsMatch(zip, @"^\d{5}$") && !zip.StartsWith("00"), "Código postal fiscal inválido");
        var regime = Text(p, "regime", 3);
        var use = Text(p, "use", 4).ToUpperInvariant();
        if (Sat.ReceiverProblem(rfc, regime, use) is { } problem) throw new BusinessException(400, problem);
        // CFDI 4.0 compares the name with the Constancia de Situación Fiscal: upper case and single spaces.
        var name = Regex.Replace(Text(p, "name", 250).Normalize().ToUpper(new System.Globalization.CultureInfo("es-MX")), @"\s+", " ").Trim();
        var receiver = J.O("rfc", rfc, "name", name, "postalCode", zip, "regime", regime, "use", use);
        var email = p.S("email").Trim();
        if (email != "") { Need(email.Length <= 150 && Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"), "Correo para la factura inválido"); receiver["email"] = email; }
        return receiver;

    }
    // How the ticket stands for invoicing: once stamped it cannot be stamped again, from the register or from the web.
    static readonly string[] FiscalStamped = ["stamped", "sandbox-stamped", "simulated-stamped", "cancellation-pending", "sandbox-cancellation-pending"];
    public static string InvoiceStatus(BranchState s, JsonObject visit) {

        var requests = s.Related("fiscal", "visitId", visit.S("id")).ToArray();
        if (requests.Any(x => FiscalStamped.Contains(x.S("status")))) return "invoiced";
        return requests.Any(x => !FiscalClosed.Contains(x.S("status"))) ? "processing" : "none";

    }
    bool TryFiscal(BranchState s, JsonObject u, string type, JsonObject p, out JsonObject result) {

        result = new();
        if (!new[] { "fiscal.request", "fiscal.token", "fiscal.cancel.request", "fiscal.portal.configure", "fiscal.retry" }.Contains(type)) return false;
        Role(u, "manager");
        // Only a submission the PAC never answered, without its id: the manager checked in the PAC portal that the folio was not
        // stamped. The retry keeps the same Folio and Date, so a stamp that did happen is found instead of duplicated.
        if (type == "fiscal.retry") { var f = s.Get("fiscal", p.S("id")); Need(f.S("status") == "pending-reconciliation" && f.S("providerId") == "", "Sólo se reintenta un envío al PAC que quedó sin respuesta; los demás estados se resuelven solos"); var reason = Text(p, "reason"); f["status"] = "pending"; f.Remove("leaseUntil"); f["retryReason"] = reason; f["retriedBy"] = u.S("id"); f["retriedAt"] = J.Now(); f["message"] = "Reintento autorizado: " + reason; result = J.O("id", f.S("id"), "status", "pending"); return true; }
        if (type == "fiscal.portal.configure") { var url = new Uri(Text(p, "url", 500)); Need(url.Scheme == "https" && url.UserInfo == "" && url.Query == "" && url.Fragment == "", "Portal HTTPS requerido"); s.Meta["invoicePortal"] = url.ToString().TrimEnd('/'); result = J.O("configured", true); return true; }
        if (type == "fiscal.cancel.request") {

            var f = s.Get("fiscal", p.S("id"));
            Need(new[] { "stamped", "sandbox-stamped", "simulated-stamped" }.Contains(f.S("status")), "Sólo un CFDI timbrado puede solicitar cancelación");
            var simulated = f.S("status") == "simulated-stamped";
            if (simulated) Simulator(s, "pac", "facturación (PAC)");
            f["status"] = simulated ? "simulated-cancelled" : f.S("status") == "sandbox-stamped" ? "sandbox-cancellation-pending" : "cancellation-pending";
            f["cancelReason"] = Text(p, "reason");
            f["cancelMotive"] = Choice(p, "motive", "01", "02", "03", "04");
            Need(f.S("cancelMotive") != "01" || Guid.TryParse(p.S("replacementUuid"), out _), "Motivo 01 requiere UUID sustituto");
            f["replacementUuid"] = p.S("replacementUuid");
            f["cancelRequestedBy"] = u.S("id");
            // The simulated PAC accepts the cancellation at once; the SAT would ask the receiver when motive 02 exceeds its thresholds.
            if (simulated) { f["cancelledAt"] = J.Now(); f["message"] = "SIMULADO · cancelación aceptada por el PAC simulado (motivo " + f.S("cancelMotive") + ")"; Log(s, u, "pac", "cancel", "cancelled", "Cancelación del UUID " + f.S("fiscalUuid"), f.S("fiscalUuid")); }
            result = J.O("id", f.S("id"), "status", f.S("status"));
            return true;

        }
        var v = s.Get("visit", p.S("visitId"));
        Need(v.S("status") == "closed", "La solicitud corresponde a un ticket liquidado");
        Need(!s.Related("fiscal", "visitId", v.S("id")).Any(x => !FiscalClosed.Contains(x.S("status"))), "Ticket ya tiene solicitud fiscal", 409);
        if (type == "fiscal.token") {

            Need(s.Meta.S("invoicePortal") != "", "Configura el portal público separado");
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var expires = DateTimeOffset.UtcNow.AddHours(24).ToString("O");
            Add(s, "invoicetoken", J.O("tokenHash", J.Hash(token), "visitId", v.S("id"), "expires", expires, "used", false, "ticketSnapshot", J.O("totals", Totals(s, v), "closedAt", v.S("closedAt"))));
            result = J.O("url", s.Meta.S("invoicePortal") + "/invoice#" + token, "expires", expires);
            return true;

        }
        var receiver = FiscalReceiver(p);
        result = Add(s, "fiscal", J.O("visitId", v.S("id"), "status", "pending", "receiver", receiver, "version", "4.0", "folio", J.Id(), "issuedLocal", BranchNow(s).ToString("yyyy-MM-ddTHH:mm:ss"), "totals", Totals(s, v), "at", J.Now(), "actor", u.S("id"), "message", "Pendiente de validación y PAC configurado; no hay timbre fiscal"));
        return true;

    }
}
