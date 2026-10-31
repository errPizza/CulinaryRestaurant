using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
namespace MesaNexo.Tests;

// One money transition across each place it can break: the store refusing before writing, the store's answer lost after
// writing, and the backend dying in between. Whatever happens, the same operation id ends in exactly one payment.
static class BoundaryTests {
    public static async Task Run(string root, List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed")); Console.WriteLine("PASS " + name); }
        var site = "boundary-site";
        var folder = Path.Combine(root, ".lab", "tests", site);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        if (Directory.Exists(TestLicense.Folder(site))) Directory.Delete(TestLicense.Folder(site), true);
        using var runtime = LabPostgres.Test(root, folder, site);
        var faults = new FaultStore(runtime);
        var service = new BranchService(faults, site, new Passwords());
        var password = "Boundary-" + J.Id();
        var enrollment = J.Id();
        await TestLicense.Initialize(service, J.O("siteId", site, "name", "Fronteras", "mode", "licencia", "managerName", "Gerencia", "managerUsername", "gerencia", "managerPassword", password, "enrollmentCode", enrollment, "modules", Restaurant.Modules));
        var token = (await service.Login(J.O("siteId", site, "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "Terminal", "username", "gerencia", "password", password, "enrollmentCode", enrollment))).S("token");
        Task<JsonObject> Send(string type, JsonObject p, string operation) => service.Action(token, J.O("operationId", operation, "type", type, "payload", p));
        async Task<JsonObject> Act(string type, JsonObject p) => (await Send(type, p, J.Id())).Obj("result");
        await Act("catalog.category", J.O("name", "Todo"));
        var product = await Act("catalog.product", J.O("name", "Plato", "price", 10000, "taxBps", 1600, "category", "Todo", "station", "Cocina"));
        await Act("cash.open", J.O("initial", 0));
        async Task<string> Account() {

            var visit = (await Act("visit.quick", new())).S("id");
            await Act("command.send", J.O("visitId", visit, "items", new[] { J.O("productId", product.S("id"), "quantity", 1) }));
            return visit;

        }
        async Task<int> Payments(string visit) => (await new BranchService(runtime, site, new Passwords()).Load()).Related("payment", "visitId", visit).Count();
        async Task Lost(Func<Task> act) { try { await act(); throw new Exception("The fault did not fire"); } catch (StoreException) { } }
        // Before the commit: not executed, and a retry executes it.
        var visit = await Account();
        var operation = J.Id();
        var pay = J.O("visitId", visit, "amount", 10000, "method", "cash");
        faults.LoseNextRequest = true;
        await Lost(() => Send("payment.add", (JsonObject)pay.DeepClone(), operation));
        Check((await service.OperationStatus(token, operation)).S("local") == "unknown" && await Payments(visit) == 0, "a failure before the commit is seen as not executed");
        await Send("payment.add", (JsonObject)pay.DeepClone(), operation);
        Check(await Payments(visit) == 1, "the retry executes it once");
        // After the commit: executed, the answer lost, and a retry changes nothing.
        visit = await Account();
        operation = J.Id();
        pay = J.O("visitId", visit, "amount", 10000, "method", "cash");
        faults.LoseNextResponse = true;
        await Lost(() => Send("payment.add", (JsonObject)pay.DeepClone(), operation));
        Check((await service.OperationStatus(token, operation)).S("local") == "committed", "a lost answer is seen as executed");
        var replay = await Send("payment.add", (JsonObject)pay.DeepClone(), operation);
        Check(await Payments(visit) == 1 && replay.Obj("result").S("visitId") == visit, "the retry returns the first result and adds nothing");
        // The backend dies after the commit: a new process finds the receipt.
        visit = await Account();
        operation = J.Id();
        pay = J.O("visitId", visit, "amount", 10000, "method", "cash");
        faults.LoseNextResponse = true;
        await Lost(() => Send("payment.add", (JsonObject)pay.DeepClone(), operation));
        var restarted = new BranchService(faults, site, new Passwords());
        TestLicense.Attach(restarted);
        Check((await restarted.OperationStatus(token, operation)).S("local") == "committed", "a restarted backend knows the operation ran");
        await restarted.Action(token, J.O("operationId", operation, "type", "payment.add", "payload", (JsonObject)pay.DeepClone()));
        Check(await Payments(visit) == 1, "and a retry after the restart adds nothing");
        // Same id, other content: never a second meaning.
        try { await restarted.Action(token, J.O("operationId", operation, "type", "payment.add", "payload", J.O("visitId", visit, "amount", 1, "method", "cash"))); throw new Exception("Id reused"); } catch (BusinessException e) { Check(e.Status == 409, "an operation id cannot be reused with other content"); }
        Check((await restarted.AuditChain()).B("valid") && (await runtime.VerifyOutbox()).B("valid"), "after every fault the chain and the outbox still verify");
        // What the API tells a client about each kind of failure.
        JsonObject Failure(int status) => ApiHost.Failure(status, "mensaje", null, "op-1");
        Check(Failure(403).S("outcome") == "not-executed" && Failure(423).S("code") == "locked", "a rejection says it did not run");
        Check(Failure(503).S("outcome") == "unknown" && Failure(409).S("outcome") == "unknown" && Failure(503).S("operationId") == "op-1", "anything else says to reconcile that operation");

    }
}
