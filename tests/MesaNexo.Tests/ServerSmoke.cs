using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
namespace MesaNexo.Tests;

// Manual check against a running CulinaryServer (wrangler dev with the test root key):
// dotnet run --project tests/MesaNexo.Tests -- <root> <python> --culinary-server <url> <siteId> <licenseId> <code>
static class ServerSmoke {
    public static async Task Run(string root, string python, string url, string site, string licenseId, string code) {

        void Say(string text) => Console.WriteLine("SMOKE " + text);
        var folder = Path.Combine(root, ".lab", "smoke-" + J.Id());
        using var runtime = new RuntimeStore(python, Path.Combine(root, "runtime", "lab_host.py"), folder, site);
        var branch = new BranchService(runtime, site, new Passwords());
        var password = "Smoke-" + J.Id();
        var enrollment = J.Id();
        await branch.Initialize(J.O("siteId", site, "name", "Prueba contra CulinaryServer", "managerName", "Gerencia", "managerUsername", "gerencia", "managerPassword", password, "enrollmentCode", enrollment));
        branch.UseLicensing(new LicenseOptions(Path.Combine(folder, "license"), url, TestLicense.HardwareFile(site), UseTpm: false));
        var activated = await branch.Activate(J.O("licenseId", licenseId, "activationCode", code));
        Say("activated " + activated.ToJsonString());
        var renewed = await branch.Renew();
        Say("renewed " + renewed.State + " validatedAt " + renewed.Payload!.S("validatedAt"));
        var token = (await branch.Login(J.O("username", "gerencia", "password", password, "deviceId", J.Id(), "deviceSecret", J.Id(), "deviceName", "smoke", "enrollmentCode", enrollment))).S("token");
        await branch.Action(token, J.O("operationId", J.Id(), "type", "ticket.create", "payload", J.O("subject", "Prueba de soporte", "message", "La impresora de cocina no responde")));
        await branch.SyncSupport();
        Say("support synced; tickets " + (await branch.Load()).List("ticket").Count(x => x.S("remoteId") != ""));
        Say("status " + (await branch.LicenseStatus()).ToJsonString());

    }
}
