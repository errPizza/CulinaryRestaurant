using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;

// The license is enforced here, on the branch server: every sign-in, state read and action passes through
// Domain.Usage, which reads the root-signed license kept verbatim in the store. Nothing derived from it is trusted.
public sealed partial class BranchService {
    LicenseOptions? licensing; ILicenseKey? licenseKey; CulinaryServer? server;
    readonly ConcurrentDictionary<string, JsonObject> verified = new();
    // Highest key floor seen, kept with the license; intermediates below it no longer verify anything.
    Jws.KeyFloor floor = Jws.KeyFloor.None;
    readonly long startedTick = Environment.TickCount64; DateTimeOffset? anchor;
    // CulinaryServer answered that this machine is not the one activated. Kept in memory: the worker asks again on start.
    bool hardwareRefused;
    // A wall clock behind what was already signed or committed by more than this is treated as tampering.
    static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(15);
    public void UseLicensing(LicenseOptions options, CulinaryServer? client = null) {

        licensing = options;
        licenseKey = null;
        server = client ?? (options.ServerUrl is null ? null : new CulinaryServer(options.ServerUrl));
        Domain.License = License;

    }
    public bool LicensingConfigured => server is not null;
    // The public half CulinaryServer certifies into the license; the private half never leaves this machine.
    public JsonObject LicenseKey() => J.O("alg", Key.Alg, "spki", Key.Spki, "binding", Key.Binding);
    ILicenseKey Key => licenseKey ??= LicenseKeys.Open(licensing ?? throw new BusinessException(503, "Las licencias no están configuradas en este servidor"));
    CulinaryServer Server => server ?? throw new BusinessException(503, "Falta la dirección de CulinaryServer");
    JsonObject Verified(string jws, string type) {

        if (verified.TryGetValue(type + jws, out var payload)) return payload;
        payload = Jws.Verify(jws, type, floor);
        if (verified.Count > 64) verified.Clear();
        verified[type + jws] = payload;
        return payload;

    }
    void UseFloor(Jws.KeyFloor next) { if (next == floor) return; floor = next; verified.Clear(); }
    public LicenseView License(BranchState s) {

        var record = s.List("license").FirstOrDefault();
        if (record is null || licensing is null) return LicenseView.Missing;
        UseFloor(floor.Max(Jws.KeyFloor.Read(record.S("keyFloor"))));
        JsonObject payload;
        JsonObject? notice;
        try { payload = Verified(record.S("license"), Jws.License); notice = record.S("notice") == "" ? null : Verified(record.S("notice"), Jws.Status); } catch (BusinessException) { return new("mismatch", null); }
        var now = Clock(s, payload, out var behind);
        // A failed TPM is a machine that needs a transfer, with the lock screen, not a server error on every request.
        string spki;
        try { spki = Key.Spki; } catch (BusinessException) { return new("mismatch", payload); }
        return LicenseView.Evaluate(payload, notice, siteId, spki, now, behind, hardwareRefused);

    }
    // The wall clock can be set back; the time since this process started cannot. The branch never reads a time
    // earlier than its last commit or than what CulinaryServer signed, and keeps counting from there.
    DateTimeOffset Clock(BranchState s, JsonObject payload, out bool behind) {

        var wall = DateTimeOffset.UtcNow;
        var floor = LicenseView.Signed(payload);
        // A commit made while the clock ran ahead must not lock the branch for good: past the offline grace after the
        // last signed time, the committed time is not trusted.
        if (DateTimeOffset.TryParse(s.Meta.S("clock"), out var committed) && committed > floor && committed <= LicenseView.OfflineUntil(payload)) floor = committed;
        anchor ??= wall > floor ? wall : floor;
        var counted = anchor.Value + TimeSpan.FromMilliseconds(Environment.TickCount64 - startedTick);
        var now = new[] { wall, floor, counted }.Max();
        behind = wall < now - ClockTolerance;
        return now;

    }
    public async Task<JsonObject> LicenseStatus(CancellationToken ct = default) {

        var s = await View(ct);
        return s.Count == 0 ? LicenseView.Missing.Summary() : License(s).Summary();

    }
    // Only a root-signed license for this site and this machine's key; a lower revision never replaces a higher one.
    public async Task<LicenseView> InstallLicense(string jws, bool activation = false, CancellationToken ct = default, string? keyFloor = null) {

        var offered = Jws.KeyFloor.Read(keyFloor);
        UseFloor(floor.Max(offered));
        var payload = Verified(jws, Jws.License);
        Need(payload.S("siteId") == siteId, "La licencia es de otra sucursal", 403);
        Need(payload.Obj("key").S("spki") == Key.Spki, "La licencia está ligada a otro equipo", 403);
        return await Exclusive(async () => {

            var s = await Load(ct);
            var record = s.List("license").FirstOrDefault();
            if (record is not null && Current(record) is { } current) {

                Need(activation || current.S("licenseId") == payload.S("licenseId"), "Para cambiar de licencia, actívala de nuevo", 409);
                if (current.S("licenseId") == payload.S("licenseId")) Need(payload.L("revision") > current.L("revision") || payload.L("revision") == current.L("revision") && LicenseView.Signed(payload) >= LicenseView.Signed(current), "Licencia anterior a la instalada", 409);

            }
            record ??= s.Add("license", J.O("id", "current", "siteId", siteId));
            record["license"] = jws;
            var stored = Jws.KeyFloor.Read(record.S("keyFloor"));
            if (stored.Max(offered) == offered && offered != stored) record["keyFloor"] = keyFloor;
            record["notice"] = "";
            record["installedAt"] = J.Now();
            await Commit(s, J.Id(), "culinary-server", J.Hash(jws), J.O("installed", true), activation ? "license.activate" : "license.renew", ct);
            return License(s);

        }, ct);

    }
    JsonObject? Current(JsonObject record) { try { return Verified(record.S("license"), Jws.License); } catch (BusinessException) { return null; } }
    // Locks at once, without waiting for the offline grace, only on a notice answering this branch's own request.
    async Task ApplyNotice(string jws, string nonce, CancellationToken ct) {

        JsonObject notice;
        try { notice = Verified(jws, Jws.Status); } catch (BusinessException) { return; }
        await Exclusive(async () => {

            var s = await Load(ct);
            var record = s.List("license").FirstOrDefault();
            var current = record is null ? null : Current(record);
            if (current is null || notice.S("licenseId") != current.S("licenseId") || notice.S("siteId") != siteId || notice.S("challenge") != nonce || notice.L("revision") < current.L("revision")) return false;
            record!["notice"] = jws;
            await Commit(s, J.Id(), "culinary-server", J.Hash(jws), J.O("locked", notice.S("status")), "license.notice", ct);
            return true;

        }, ct);

    }
    // Signed call as this branch's license; a refusal carrying a valid notice locks the branch before it is rethrown.
    async Task<JsonObject> Call(HttpMethod method, string path, JsonObject? body, CancellationToken ct) {

        var record = (await View(ct)).List("license").FirstOrDefault();
        var current = record is null ? null : Current(record);
        Need(current is not null, "Activa primero la licencia de Culinary Restaurant", 423);
        try { return await Server.Signed(method, path, current!.S("licenseId"), Key, body, ct); } catch (ServerRejection e) when (e.Notice is not null) { await ApplyNotice(e.Notice, e.Nonce, ct); throw; }

    }
    JsonObject Binding() => Key.Binding == "tpm" ? J.O("kind", "tpm") : J.O("kind", "fingerprint", "fingerprint", HardwareIdentity.Fingerprint(licensing!.HardwareFile));
    // First activation, or a new one after the provider released the machine: allowed only while the branch is locked.
    public async Task<JsonObject> Activate(JsonObject p, CancellationToken ct = default) {

        var state = await View(ct);
        // The license is stored in the branch's records; with none yet, the code would be spent and nothing kept.
        Need(state.Count > 0, "Configura primero el restaurante; después activa la licencia", 409);
        Need(License(state).Usable is false, "Culinary Restaurant ya tiene una licencia vigente", 409);
        Need(Guid.TryParse(p.S("licenseId").Trim(), out var id), "Escribe el identificador de licencia que te dio tu proveedor");
        var code = Text(p, "activationCode", 40);
        var key = Key;
        var body = J.O("activationCode", code, "siteId", siteId, "key", J.O("alg", key.Alg, "spki", key.Spki), "binding", Binding());
        JsonObject reply;
        try { reply = await Server.Signed(HttpMethod.Post, "/branch/v1/activate", id.ToString("D"), key, body, ct); } catch (ServerRejection e) {
            // A code already spent by this same machine (the install failed after CulinaryServer accepted it): only this
            // machine's key can sign the renewal, so the license comes back without a new code.
            if (e.Code == "activation_rejected" && await Recover(id, key, ct) is { } recovered) reply = recovered;
            else throw new BusinessException(e.Status is 400 or 401 or 403 or 409 ? 403 : 502, e.Code == "activation_rejected" ? "Código de activación inválido, ya usado o de una licencia cancelada" : e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { throw new BusinessException(502, "No se pudo contactar a CulinaryServer; revisa que el equipo tenga Internet e inténtalo de nuevo"); }
        return (await InstallLicense(reply.S("license"), true, ct, reply.S("keyFloor"))).Summary();

    }
    async Task<JsonObject?> Recover(Guid id, ILicenseKey key, CancellationToken ct) {

        var body = key.Binding == "tpm" ? new JsonObject() : J.O("fingerprint", HardwareIdentity.Fingerprint(licensing!.HardwareFile));
        try { return await Server.Signed(HttpMethod.Post, "/branch/v1/license/renew", id.ToString("D"), key, body, ct); } catch (ServerRejection) { return null; }

    }
    public async Task<LicenseView> Renew(CancellationToken ct = default) {

        var body = Key.Binding == "tpm" ? new JsonObject() : J.O("fingerprint", HardwareIdentity.Fingerprint(licensing!.HardwareFile));
        JsonObject reply;
        try { reply = await Call(HttpMethod.Post, "/branch/v1/license/renew", body, ct); } catch (ServerRejection e) when (e.Code == "binding_mismatch") { hardwareRefused = true; throw; }
        hardwareRefused = false;
        return await InstallLicense(reply.S("license"), false, ct, reply.S("keyFloor"));

    }
}
