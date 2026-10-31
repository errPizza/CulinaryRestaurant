using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Api;

public sealed partial class BranchService {
    // For culinaryos-reset-password: the way back when no manager can sign in. It needs root on the server, which already holds
    // the keyring, and it leaves its mark in the audit chain under its own operation.
    public async Task<JsonObject> ResetPassword(string username, string password, CancellationToken ct = default) {

        var hash = passwords.Hash(password);
        return await Exclusive(async () => {

            var s = await Load(ct);
            var user = s.Related("user", "username", username).FirstOrDefault() ?? throw new BusinessException(404, "No existe el usuario " + username);
            user["password"] = hash;
            var sessions = s.Related("session", "userId", user.S("id")).Where(x => !x.B("revoked")).ToArray();
            foreach (var session in sessions) session["revoked"] = true;
            var result = J.O("username", username, "role", user.S("role"), "active", user.B("active"), "sessionsClosed", sessions.Length);
            await Commit(s, J.Id(), "local-maintenance", J.Hash("password-reset:" + user.S("id") + ":" + J.Now()), result, "user.password.reset", ct);
            return result;

        }, ct);

    }
}
