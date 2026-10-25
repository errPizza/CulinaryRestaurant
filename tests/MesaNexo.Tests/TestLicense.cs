using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
namespace MesaNexo.Tests;

// Signs licenses with the test root (tests/keys), whose public half Debug builds and the test scripts embed.
// Never the provider's production root: tools/build.ps1 refuses to publish with this key.
static class TestLicense {
    static readonly Lazy<byte[]> Seed = new(() => {

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) {

            var file = Path.Combine(dir.FullName, "tests", "keys", "root-private.pem");
            if (!File.Exists(file)) continue;
            var der = Convert.FromBase64String(string.Concat(File.ReadAllLines(file).Where(x => !x.StartsWith("-----"))));
            return der[^32..];

        }
        throw new FileNotFoundException("tests/keys/root-private.pem");

    });
    public static string Sign(string type, JsonObject payload) {

        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(J.O("alg", "EdDSA", "typ", type, "kid", Jws.RootKid).ToJsonString()));
        var body = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(Seed.Value));
        var input = Encoding.ASCII.GetBytes(header + "." + body);
        signer.BlockUpdate(input, 0, input.Length);
        return header + "." + body + "." + Base64Url.EncodeToString(signer.GenerateSignature());

    }
    public static string Folder(string site) => Path.Combine(Path.GetTempPath(), "culinary-test-license", site);
    public static string LicenseId(string site) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes("license:" + site))).ToString("D");
    // A machine without TPM: the fingerprint parts come from a file, as the root step writes them on CulinaryOS.
    public static string HardwareFile(string site, string board = "4c4c4544-0042-3510-8051-b4c04f563232") {

        Directory.CreateDirectory(Folder(site));
        var file = Path.Combine(Folder(site), "hardware.json");
        File.WriteAllText(file, J.O("board", board, "disk", "S4EVNX0N" + site.Length, "mac", "52:54:00:12:34:56").ToJsonString());
        return file;

    }
    public static void Attach(BranchService branch, CulinaryServer? server = null) => branch.UseLicensing(new LicenseOptions(Folder(branch.SiteId), HardwareFile: HardwareFile(branch.SiteId), UseTpm: false), server);
    public static JsonObject Payload(BranchService branch, string[] features, long maxUsers = 15, long maxTables = 30, string plan = "annual", DateTimeOffset? expires = null, DateTimeOffset? validated = null, long revision = 1, string? site = null, string? spki = null, string tier = "pro", string[]? addons = null, long maxTerminals = 100) {

        var now = validated ?? DateTimeOffset.UtcNow;
        string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        return J.O("v", 1, "licenseId", LicenseId(branch.SiteId), "revision", revision, "tenantId", "tenant-" + branch.SiteId, "siteId", site ?? branch.SiteId, "customer", "Restaurante de prueba", "branchName", "Centro", "plan", plan, "tier", tier, "addons", addons ?? ["mobileOrdering", "deliveryHub", "webOrdering"], "status", "active", "features", features, "limits", J.O("maxUsers", maxUsers, "maxTables", maxTables, "maxTerminals", maxTerminals), "activatedAt", Iso(now.AddDays(-1)), "expiresAt", plan == "permanent" ? null : Iso(expires ?? now.AddYears(1)), "validatedAt", Iso(now), "offlineUntil", Iso(now.AddDays(3)), "key", J.O("alg", "ES256", "spki", spki ?? branch.LicenseKey().S("spki")), "binding", "fingerprint", "iat", Iso(now));

    }
    // The license a test branch was configured with before licenses existed: its modules and plan limits.
    public static async Task Grant(BranchService branch, JsonObject config) {

        Attach(branch);
        await branch.InstallLicense(Sign(Jws.License, Payload(branch, config.A("modules").Strings(), config.L("maxUsers", 15), config.L("maxTables", 30))), true);

    }
    public static async Task Initialize(BranchService branch, JsonObject config) { await branch.Initialize(config); await Grant(branch, config); }
}
