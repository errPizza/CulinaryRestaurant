using System.Globalization;
using System.Text.Json.Nodes;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;

// The time clock and dishes sent back to the kitchen. Both feed the owner's panel: hours worked give the labour cost,
// and a return says why a delivered dish came back. Neither changes what an account owes.
public sealed partial class Restaurant {
    public static readonly string[] ReturnReasons = ["cold", "doneness", "foreign", "taste", "wrong", "late", "other"];
    public static readonly string[] CategoryKinds = ["food", "drink", "alcohol", "other"];
    static readonly TimeSpan LongestShift = TimeSpan.FromHours(24);
    // A lot's best-before date, as the day printed on the package.
    static string ExpiryDate(JsonNode p) {

        var text = p.S("expiresOn").Trim();
        if (text == "") return "";
        Need(DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _), "expiresOn: usa una fecha AAAA-MM-DD");
        return text;

    }
    static JsonObject? OpenShift(BranchState s, string userId) => s.Related("attendance", "userId", userId).FirstOrDefault(x => x.S("outAt") == "");
    // Someone clocks themselves; a manager can do it for anyone who forgot.
    JsonObject ClockTarget(BranchState s, JsonObject u, JsonObject p) {

        var userId = p.S("userId") is { Length: > 0 } other ? other : u.S("id");
        if (userId != u.S("id")) Role(u, "manager");
        var user = s.Get("user", userId);
        Need(user.B("active", true), "Empleado desactivado");
        return user;

    }
    bool TryWorkforce(BranchState s, JsonObject u, string type, JsonObject p, out JsonObject result) {

        result = J.O("ok", true);
        var id = p.S("id");
        switch (type) {
            case "clock.in": {

                    var user = ClockTarget(s, u, p);
                    Need(OpenShift(s, user.S("id")) is null, user.S("name") + " ya registró su entrada", 409);
                    result = Add(s, "attendance", J.O("userId", user.S("id"), "inAt", J.Now(), "outAt", "", "actor", u.S("id")));
                    break;

                }
            case "clock.out": {

                    var user = ClockTarget(s, u, p);
                    var shift = OpenShift(s, user.S("id"));
                    Need(shift is not null, user.S("name") + " no tiene una entrada abierta", 409);
                    shift!["outAt"] = J.Now();
                    shift["outActor"] = u.S("id");
                    result = shift;
                    break;

                }
            // A forgotten exit, or a shift nobody clocked. Another manager corrects your own hours.
            case "clock.fix": {

                    Role(u, "manager");
                    var shift = id != "" ? s.Get("attendance", id) : null;
                    var userId = shift?.S("userId") ?? s.Get("user", p.S("userId")).S("id");
                    Need(userId != u.S("id"), "Tu asistencia la corrige otro gerente", 403);
                    Need(DateTimeOffset.TryParse(p.S("inAt"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var inAt), "inAt: fecha y hora inválidas");
                    Need(DateTimeOffset.TryParse(p.S("outAt"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var outAt), "outAt: fecha y hora inválidas");
                    Need(inAt < outAt && outAt - inAt <= LongestShift, "La salida debe ir después de la entrada y en menos de 24 horas");
                    Need(outAt <= DateTimeOffset.UtcNow.AddMinutes(5), "La salida no puede estar en el futuro");
                    Need(!s.Related("attendance", "userId", userId).Any(x => x.S("id") != id && DateTimeOffset.TryParse(x.S("inAt"), out var a) && (x.S("outAt") == "" || DateTimeOffset.Parse(x.S("outAt")) > inAt) && a < outAt), "Se encima con otro turno del empleado");
                    shift ??= Add(s, "attendance", J.O("userId", userId, "actor", u.S("id")));
                    shift["inAt"] = inAt.ToUniversalTime().ToString("O");
                    shift["outAt"] = outAt.ToUniversalTime().ToString("O");
                    shift["fixedBy"] = u.S("id");
                    shift["fixedAt"] = J.Now();
                    shift["fixReason"] = Text(p, "reason", 200);
                    result = shift;
                    break;

                }
            // The guest sent a delivered dish back. It stays charged; remaking it sends it to the kitchen again.
            case "unit.return": {

                    Role(u, "mesero", "manager");
                    var unit = s.Get("unit", id);
                    var v = Own(s, u, unit.S("visitId"));
                    Need(v.S("status") == "open", "Cuenta cerrada");
                    Need(unit.S("status") == "delivered", "Solo se devuelve un platillo entregado");
                    var reason = Choice(p, "reason", ReturnReasons);
                    result = Add(s, "kitchenreturn", J.O("unitId", unit.S("id"), "visitId", v.S("id"), "productId", unit.S("productId"), "name", unit.S("name"), "station", unit.S("station"), "reason", reason, "notes", Sanitize(p.S("notes"), 200), "remake", p.B("remake", true), "actor", u.S("id"), "at", J.Now()));
                    if (p.B("remake", true)) {

                        unit["status"] = "received";
                        unit["receivedAt"] = J.Now();
                        unit["startedAt"] = null;
                        unit["readyAt"] = null;
                        unit["deliveredAt"] = null;
                        if (s.Related("command", "id", unit.S("commandId")).FirstOrDefault() is { } command) command["completeNotified"] = false;

                    }
                    History(unit, u, unit.S("status"), "Devolución: " + reason);
                    break;

                }
            default:
                return false;
        }
        return true;

    }
}
