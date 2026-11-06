using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Api;
using MesaNexo.Core;
using MesaNexo.Integrations;
namespace MesaNexo.Tests;

// A CSD shaped like the SAT's: RFC in x500UniqueIdentifier, key usage for sealing, the serial as 20 ASCII digits, and the
// private key as PKCS#8 with PBES2 and des-ede3-cbc (tests/fixtures/csd, made with `openssl pkcs8 -topk8 -v2 des3`).
static class CsdFixture {
    public const string Password = "12345678a";
    public const string Rfc = "EKU9003173C9";
    public static string Root() {

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) if (File.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "csd", "llave-prueba-des3.key"))) return dir.FullName;
        throw new FileNotFoundException("tests/fixtures/csd/llave-prueba-des3.key");

    }
    public static byte[] Key() => File.ReadAllBytes(Path.Combine(Root(), "tests", "fixtures", "csd", "llave-prueba-des3.key"));
    public static byte[] Certificate(string rfc = Rfc, string serial = "30001000000500003416", X509KeyUsageFlags usage = X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null, string issuer = "SERVICIO DE ADMINISTRACION TRIBUTARIA", RSA? key = null) {

        using var own = RSA.Create();
        if (key is null) own.ImportEncryptedPkcs8PrivateKey(Password, Key(), out _);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName("ESCUELA KEMPER URGATE");
        subject.Add("2.5.4.45", rfc + " / VADA800927HSRSRL05", UniversalTagNumber.PrintableString);
        subject.AddOrganizationName("ESCUELA KEMPER URGATE");
        var request = new CertificateRequest(subject.Build(), key ?? own, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
        using var authority = RSA.Create(2048);
        var now = DateTimeOffset.UtcNow;
        using var cert = request.Create(new X500DistinguishedName("CN=AC DE PRUEBA, O=" + issuer), X509SignatureGenerator.CreateForRSA(authority, RSASignaturePadding.Pkcs1), notBefore ?? now.AddDays(-30), notAfter ?? now.AddYears(4), Encoding.ASCII.GetBytes(serial));
        return cert.RawData;

    }
}
// Loads nothing anywhere: records what it was given and answers as configured.
sealed class RecordingCsdLoader(bool sandbox = true): ICsdLoader {
    public bool IsSandbox => sandbox; public int Calls; public PacResult Answer = new("loaded", Message: "Sellos cargados", HttpStatus: 204); public byte[] LastCertificate = [];
    public Task<PacResult> LoadCsd(byte[] certificate, byte[] privateKey, string password, CancellationToken ct = default) { Calls++; LastCertificate = certificate.ToArray(); return Task.FromResult(Answer); }
}
static class CsdTests {
    public static async Task Run(List<JsonObject> evidence) {

        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); evidence.Add(J.O("test", name, "result", "passed", "pac", "synthetic; no network")); Console.WriteLine("PASS " + name); }
        void Deny(Action action, string contains, string name) { try { action(); throw new Exception("Expected rejection: " + name); } catch (BusinessException e) { Check(e.Message.Contains(contains), name + " (" + e.Message + ")"); } }

        // RFC and receiver rules shared with CulinaryServer.
        Check(new[] { "EKU9003173C9", "XIA190128J61", "CACX7605101P8", "SAT970701NN3" }.All(r => Sat.RfcProblem(r) is null), "RFC check digit accepts the SAT's own test RFCs");
        Check(Sat.RfcProblem("EKU9003173C8")!.Contains("dígito verificador") && Sat.RfcProblem("EKU9013173C9")!.Contains("fecha") && Sat.RfcProblem("EKU900317")!.Contains("homoclave"), "RFC with a wrong check digit, date or length is refused");
        Check(Sat.ReceiverProblem("EKU9003173C9", "612", "G03")!.Contains("persona moral") && Sat.ReceiverProblem("EKU9003173C9", "601", "D01")!.Contains("persona física") && Sat.ReceiverProblem("CACX7605101P8", "612", "D01") is null, "receiver regime and CFDI use must apply to the kind of person");
        Check(Sat.ReceiverProblem(Sat.GenericRfc, "601", "G03")!.Contains("616") && Sat.ReceiverProblem(Sat.GenericRfc, "616", "S01") is null && Sat.ReceiverProblem("EKU9003173C9", "999", "G03")!.Contains("catálogo"), "público en general only with 616/S01, and regimes come from the SAT catalog");

        // The CSD itself.
        var key = CsdFixture.Key();
        var cer = CsdFixture.Certificate();
        var now = DateTimeOffset.UtcNow;
        var info = CsdCheck.Validate(cer, key, CsdFixture.Password, CsdFixture.Rfc, now, production: true);
        Check(info.Rfc == CsdFixture.Rfc && info.Serial == "30001000000500003416" && info.NotAfter > now, "a valid CSD with a PBES2/3DES key opens and reports RFC, SAT serial and expiry");
        Deny(() => CsdCheck.Validate(cer, key, "otra-contraseña", CsdFixture.Rfc, now, true), "contraseña", "a wrong password is refused");
        using (var other = RSA.Create(2048)) Deny(() => CsdCheck.Validate(CsdFixture.Certificate(key: other), key, CsdFixture.Password, CsdFixture.Rfc, now, true), "no corresponde", "a .key from other sellos is refused");
        Deny(() => CsdCheck.Validate(CsdFixture.Certificate(usage: X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation | X509KeyUsageFlags.DataEncipherment | X509KeyUsageFlags.KeyAgreement), key, CsdFixture.Password, CsdFixture.Rfc, now, true), "e.firma", "an e.firma (FIEL) is told apart from a CSD");
        Deny(() => CsdCheck.Validate(CsdFixture.Certificate(notBefore: now.AddYears(-5), notAfter: now.AddDays(-1)), key, CsdFixture.Password, CsdFixture.Rfc, now, true), "venció", "an expired CSD is refused before it reaches the PAC");
        Deny(() => CsdCheck.Validate(CsdFixture.Certificate(rfc: "XIA190128J61"), key, CsdFixture.Password, CsdFixture.Rfc, now, true), "XIA190128J61", "a CSD of another RFC than the license's is refused");
        Deny(() => CsdCheck.Validate(CsdFixture.Certificate(issuer: "OTRA AUTORIDAD"), key, CsdFixture.Password, CsdFixture.Rfc, now, true), "SAT", "in production the certificate must come from the SAT");
        Check(CsdCheck.Validate(CsdFixture.Certificate(issuer: "OTRA AUTORIDAD"), key, CsdFixture.Password, CsdFixture.Rfc, now, false).Rfc == CsdFixture.Rfc, "the sandbox accepts the SAT test authority's certificates");
        Deny(() => CsdCheck.Validate(Encoding.ASCII.GetBytes("no es un certificado"), key, CsdFixture.Password, CsdFixture.Rfc, now, true), ".cer", "a file that is not a certificate is refused");

        // The envelope: only this license, this sending and this intake key open it.
        using var intake = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spki = intake.ExportSubjectPublicKeyInfo();
        var sha = Convert.ToHexStringLower(SHA256.HashData(spki));
        var plain = Encoding.UTF8.GetBytes(J.O("cer", Convert.ToBase64String(cer), "key", Convert.ToBase64String(key), "password", CsdFixture.Password).ToJsonString());
        var context = IntakeEnvelope.Context("licencia-1", "envio-1", sha);
        var envelope = IntakeEnvelope.Seal(spki, plain, context);
        Check(IntakeEnvelope.Open(intake, envelope, context).SequenceEqual(plain), "the branch opens what was sealed to its intake key");
        Check(Throws(() => IntakeEnvelope.Open(intake, envelope, IntakeEnvelope.Context("licencia-2", "envio-1", sha))), "an envelope does not open for another license");
        Check(Throws(() => IntakeEnvelope.Open(intake, envelope, IntakeEnvelope.Context("licencia-1", "envio-2", sha))), "an envelope does not open as another sending");
        var tampered = (JsonObject)envelope.DeepClone();
        var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(tampered.S("ct"));
        bytes[5] ^= 1;
        tampered["ct"] = System.Buffers.Text.Base64Url.EncodeToString(bytes);
        Check(Throws(() => IntakeEnvelope.Open(intake, tampered, context)), "a modified envelope is refused by AES-GCM");
        using (var stranger = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)) Check(Throws(() => IntakeEnvelope.Open(stranger, envelope, context)), "another machine's intake key cannot open it");

        // An envelope sealed by Culinary Dueño's own code (src/csd.ts, WebCrypto), kept as a vector in tests/vectors.
        var vectorFile = Path.Combine(CsdFixture.Root(), "tests", "vectors", "csd-envelope.json");
        var vector = JsonNode.Parse(File.ReadAllText(vectorFile))!.AsObject();
        using (var branchKey = ECDiffieHellman.Create()) {

            branchKey.ImportPkcs8PrivateKey(System.Buffers.Text.Base64Url.DecodeFromChars(vector.S("intakePkcs8")), out _);
            var opened = JsonNode.Parse(IntakeEnvelope.Open(branchKey, vector.Obj("envelope"), IntakeEnvelope.Context(vector.S("licenseId"), vector.S("submissionId"), vector.S("intakeSha256"))))!.AsObject();
            Check(opened.S("cer") == vector.Obj("expected").S("cer") && opened.S("key") == vector.Obj("expected").S("key") && opened.S("password") == vector.Obj("expected").S("password"), "the branch opens an envelope sealed with WebCrypto by Culinary Dueño");

        }

        // Facturama's API Web: PUT TaxEntity/UploadCsd with both files in base64 answers 204.
        HttpRequestMessage? seen = null; JsonObject? sent = null;
        var answer = HttpStatusCode.NoContent; var answerBody = "";
        using (var adapter = new FacturamaAdapter("user", "pass", true, new ContractHttp(async req => { seen = req; sent = JsonNode.Parse(await req.Content!.ReadAsStringAsync())!.AsObject(); return new HttpResponseMessage(answer) { Content = new StringContent(answerBody, Encoding.UTF8, "application/json") }; }))) {

            var loaded = await adapter.LoadCsd(cer, key, CsdFixture.Password);
            Check(loaded.Status == "loaded" && loaded.HttpStatus == 204 && seen!.Method == HttpMethod.Put && seen.RequestUri!.AbsoluteUri == "https://apisandbox.facturama.mx/TaxEntity/UploadCsd", "the CSD goes to PUT TaxEntity/UploadCsd and 204 means loaded");
            Check(sent!.S("Certificate") == Convert.ToBase64String(cer) && sent.S("PrivateKey") == Convert.ToBase64String(key) && sent.S("PrivateKeyPassword") == CsdFixture.Password && sent.Count == 3, "the body carries exactly the certificate, the key in base64 and the password");
            answer = HttpStatusCode.BadRequest; answerBody = J.O("Message", "El certificado no corresponde a la cuenta").ToJsonString();
            var refused = await adapter.LoadCsd(cer, key, CsdFixture.Password);
            Check(refused.Status == "rejected" && refused.HttpStatus == 400 && refused.Message.Contains("no corresponde") && !refused.Message.Contains(CsdFixture.Password), "a refusal is reported with Facturama's reason and without the password");
            answer = HttpStatusCode.ServiceUnavailable; answerBody = "";
            Check((await adapter.LoadCsd(cer, key, CsdFixture.Password)).Status == "unavailable", "a PAC that is down leaves the sending for the next look");

        }

    }
    static bool Throws(Action action) { try { action(); return false; } catch (Exception e) when (e is CryptographicException or BusinessException) { return true; } }
}
