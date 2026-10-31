using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
namespace MesaNexo.Tests;

static class ApprovalTests {
    public static async Task Run(string root, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        async Task Deny(Func<Task> act, string text, string name) { try { await act(); throw new Exception("Rejection expected: " + name); } catch (BusinessException e) { Check(e.Message.Contains(text), name + " · " + e.Message); } }
        var site = "approval-site";
        var folder = Path.Combine(root, ".lab", "tests", site);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        if (Directory.Exists(TestLicense.Folder(site))) Directory.Delete(TestLicense.Folder(site), true);
        using var runtime = LabPostgres.Test(root, folder, site);
        var service = new BranchService(runtime, site, new Passwords());
        var password = "Approval-" + J.Id();
        var enrollment = J.Id();
        await TestLicense.Initialize(service, J.O("siteId", site, "name", "Aprobaciones", "mode", "licencia", "managerName", "Gerencia", "managerUsername", "gerencia", "managerPassword", password, "enrollmentCode", enrollment, "modules", Restaurant.Modules));
        var token = (await service.Login(J.O("siteId", site, "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "Terminal", "username", "gerencia", "password", password, "enrollmentCode", enrollment))).S("token");
        async Task<JsonObject> Act(string type, JsonObject p, string? auth = null) => (await service.Action(auth ?? token, J.O("operationId", J.Id(), "type", type, "payload", p))).Obj("result");
        var me = (await service.Load()).List("user").Single(x => x.S("username") == "gerencia").S("id");
        var second = (await Act("user.save", J.O("name", "Segunda", "username", "segunda", "role", "manager", "password", password))).S("id");
        var supervisor = (await Act("user.save", J.O("name", "Supervisor", "username", "supervisor", "role", "manager", "password", password, "approvalLimit", 50000))).S("id");
        var waiter = (await Act("user.save", J.O("name", "Mesero", "username", "mesero", "role", "mesero", "password", password))).S("id");
        await Act("user.pin", J.O("id", me, "passcode", "4826"));
        await Act("user.pin", J.O("id", second, "passcode", "7391"));
        await Act("user.pin", J.O("id", supervisor, "passcode", "5172"));
        await Deny(() => Act("user.save", J.O("id", me, "name", "Gerencia", "username", "gerencia", "role", "manager", "approvalLimit", 1)), "otro gerente", "nobody changes their own limit");
        await Act("catalog.category", J.O("name", "Cava"));
        var expensive = await Act("catalog.product", J.O("name", "Reserva", "price", 200000, "taxBps", 1600, "category", "Cava", "station", "Cocina"));
        var cheap = await Act("catalog.product", J.O("name", "Agua", "price", 5000, "taxBps", 1600, "category", "Cava", "station", "Cocina"));
        var cash = await Act("cash.open", J.O("initial", 500000));
        async Task<string> Sale(string product) {

            var visit = (await Act("visit.quick", new())).S("id");
            foreach (var unit in (await Act("command.send", J.O("visitId", visit, "items", new[] { J.O("productId", product, "quantity", 1) }))).A("unitIds").Strings()) foreach (var status in new[] { "preparing", "ready", "delivered" }) await Act("unit.state", J.O("id", unit, "status", status));
            var total = (await service.Load()).Related("unit", "visitId", visit).Sum(x => x.L("price"));
            var pay = await Act("payment.add", J.O("visitId", visit, "amount", total, "method", "cash"));
            await Act("visit.close", J.O("id", visit));
            return pay.S("id");

        }
        Task<JsonObject> Authorize(string approver, string pin, string type, JsonObject payload) => Act("authorize.pin", J.O("approverId", approver, "passcode", pin, "action", J.O("type", type, "payload", payload)));
        // Refunds.
        var small = await Sale(cheap.S("id"));
        Check((await Act("payment.refund", J.O("id", small, "reason", "Sin hielo"))).L("amount") == 5000, "under the threshold one manager is enough");
        var big = await Sale(expensive.S("id"));
        await Deny(() => Act("payment.refund", J.O("id", big, "reason", "Por mi cuenta")), "otro gerente", "above the threshold one manager is not enough");
        await Deny(() => Authorize(me, "4826", "payment.refund", J.O("id", big, "reason", "Conmigo mismo")), "otro gerente", "nobody is their own second approval");
        await Deny(() => Authorize(supervisor, "5172", "payment.refund", J.O("id", big, "reason", "Fuera de su límite")), "límite", "a supervisor cannot approve past their limit");
        var bad = await Authorize(second, "0000", "payment.refund", J.O("id", big, "reason", "PIN equivocado"));
        Check(!bad.B("ok") && (await service.Load()).List("refund").All(x => x.S("paymentId") != big), "a wrong PIN executes nothing");
        var approved = await Authorize(second, "7391", "payment.refund", J.O("id", big, "reason", "Botella defectuosa"));
        Check(approved.B("ok") && approved.Obj("result").S("requestedBy") == me && approved.Obj("result").S("approvedBy") == second, "the refund records who asked and who authorized");
        var log = (await service.Load()).List("pinauth").Last(x => x.B("ok"));
        Check(log.S("actor") == me && log.S("approverId") == second && log.S("reason") == "Botella defectuosa" && log.L("amount") == 200000 && log.S("result") == "applied", "the authorization keeps requester, approver, reason and result");
        // PIN lockout.
        for (var i = 0; i < 5; i++) await Authorize(second, "0000", "payment.refund", J.O("id", small, "reason", "x"));
        var state = await service.Load();
        Check(state.List("ownernotice").Any(x => x.S("type") == "pin-locked" && x.S("userId") == second), "a locked PIN raises an alert");
        await Deny(() => Authorize(second, "7391", "payment.refund", J.O("id", small, "reason", "x")), "bloqueado", "a locked PIN stops even the right code");
        await Act("user.pin", J.O("id", second, "passcode", "7391"));
        // Cancellations: someone else asking is already a second person.
        var visit = (await Act("visit.quick", new())).S("id");
        var sent = await Act("command.send", J.O("visitId", visit, "items", new[] { J.O("productId", expensive.S("id"), "quantity", 2) }));
        var units = sent.A("unitIds").Strings();
        var own = await Act("request.create", J.O("visitId", visit, "unitId", units[0], "type", "cancel", "reason", "Lo pedí yo"));
        await Deny(() => Act("request.decide", J.O("id", own.S("id"), "approve", true)), "otro gerente", "a manager does not approve their own expensive cancellation");
        Check((await Authorize(second, "7391", "request.decide", J.O("id", own.S("id"), "approve", true))).B("ok"), "another manager can");
        var waiterEnroll = J.Id() + J.Id();
        await service.RotateEnrollment(waiterEnroll);
        var waiterToken = (await service.Login(J.O("siteId", site, "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "Comandero", "username", "mesero", "password", password, "enrollmentCode", waiterEnroll))).S("token");
        await Act("visit.transfer", J.O("id", visit, "waiterId", waiter));
        var asked = await Act("request.create", J.O("visitId", visit, "unitId", units[1], "type", "cancel", "reason", "El cliente se fue"), waiterToken);
        Check((await Act("request.decide", J.O("id", asked.S("id"), "approve", true))).Count >= 0, "a waiter's request decided by a manager already has two people");
        // Adjustments.
        await Act("cash.close", J.O("id", cash.S("id"), "counted", 0, "notes", "Cierre"));
        await Deny(() => Act("cash.adjust", J.O("id", cash.S("id"), "method", "cash", "direction", "decrease", "amount", 150000, "reason", "Faltante")), "otro gerente", "an expensive adjustment needs a second person");
        Check((await Authorize(second, "7391", "cash.adjust", J.O("id", cash.S("id"), "method", "cash", "direction", "decrease", "amount", 150000, "reason", "Faltante"))).B("ok"), "with one it goes through");
        var warehouse = (await Act("inventory.warehouse", J.O("name", "Principal", "costMethod", "average"))).S("id");
        var bottle = (await Act("inventory.ingredient", J.O("name", "Botella", "unit", "unit"))).S("id");
        await Act("stock.receive", J.O("warehouseId", warehouse, "ingredientId", bottle, "quantity", 10, "unit", "unit", "cost", 500000, "reference", "L1"));
        var count = J.O("warehouseId", warehouse, "lines", new[] { J.O("ingredientId", bottle, "quantity", 5, "unit", "unit") });
        await Deny(() => Act("stock.count", (JsonObject)count.DeepClone()), "otro gerente", "an expensive shortage needs a second person");
        Check((await Authorize(second, "7391", "stock.count", (JsonObject)count.DeepClone())).B("ok"), "with one the count is applied");
        // The threshold.
        await Deny(() => Act("dual.configure", J.O("amount", 1)), "otro gerente", "the threshold is never changed alone");
        Check((await Authorize(second, "7391", "dual.configure", J.O("amount", 3000))).B("ok"), "two managers change the threshold");
        await Act("cash.open", J.O("initial", 0));
        var nowSmall = await Sale(cheap.S("id"));
        await Deny(() => Act("payment.refund", J.O("id", nowSmall, "reason", "Ya es caro")), "otro gerente", "the new threshold applies at once");
        // Password reset from the server: for when no manager can sign in.
        var resetEnroll = J.Id() + J.Id();
        await service.RotateEnrollment(resetEnroll);
        var resetDevice = J.O("siteId", site, "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "Reinicio", "username", "segunda", "password", password, "enrollmentCode", resetEnroll);
        var resetToken = (await service.Login((JsonObject)resetDevice.DeepClone())).S("token");
        var fresh = "Nueva-" + J.Id();
        var reset = await service.ResetPassword("segunda", fresh);
        Check(reset.L("sessionsClosed") == 1 && reset.S("role") == "manager", "a reset reports what it did");
        try { await service.State(resetToken); throw new Exception("Session survived the reset"); } catch (BusinessException e) { Check(e.Status is 401 or 403, "a reset closes the user's sessions"); }
        try { await service.Login((JsonObject)resetDevice.DeepClone()); throw new Exception("Old password accepted"); } catch (BusinessException e) { Check(e.Status == 401, "the old password stops working"); }
        var renewed = (JsonObject)resetDevice.DeepClone();
        renewed["password"] = fresh;
        renewed["enrollmentCode"] = "";
        Check((await service.Login(renewed)).S("token") != "", "the new password signs in");
        var unknownDevice = (JsonObject)resetDevice.DeepClone();
        unknownDevice["deviceId"] = J.Id();
        unknownDevice["password"] = fresh;
        await Deny(() => service.Login((JsonObject)unknownDevice.DeepClone()), "ya usado", "a spent code says so instead of asking for one");
        unknownDevice["enrollmentCode"] = " ";
        await Deny(() => service.Login((JsonObject)unknownDevice.DeepClone()), "escribe el código", "a new device without a code is told to enter it");
        var pastedCode = J.Id() + J.Id();
        await service.RotateEnrollment(pastedCode);
        unknownDevice["enrollmentCode"] = "  " + pastedCode + " ";
        Check((await service.Login((JsonObject)unknownDevice.DeepClone())).S("token") != "", "spaces around a pasted code do not reject it");
        await Deny(() => service.ResetPassword("nadie", fresh), "No existe", "an unknown user is refused");
        await Deny(() => service.ResetPassword("segunda", "corta"), "12 a 200", "a short password is refused");
        state = await service.Load();
        Check(state.List("audit").Any(x => x.S("operation") == "user.password.reset" && x.S("actor") == "local-maintenance") && !state.Committed.Records.Values.Any(x => x.Data.ToJsonString().Contains(fresh)), "the reset is in the audit chain and the password is stored nowhere");
        Check((await service.AuditChain()).B("valid"), "the audit chain still verifies");
        // A session that ended.
        await Act("session.logout", new(), waiterToken);
        try { await service.State(waiterToken); throw new Exception("Revoked session served"); } catch (BusinessException e) { Check(e.Status is 401 or 403, "a closed session authorizes nothing"); }

    }
}
