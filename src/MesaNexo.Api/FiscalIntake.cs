using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;

// The issuer's CSD reaches the branch from Culinary Dueño through CulinaryServer, sealed to this machine's intake key:
// ECDH P-256, HKDF-SHA-256 and AES-256-GCM, with the license, the sending and the key bound as associated data. The branch
// checks it, loads it into the PAC and drops it. Only the RFC, serial number and validity stay; the .key and its password
// are never written anywhere, so there is nothing to give back, not even to the owner.
public sealed record CsdInfo(string Rfc, string Serial, DateTimeOffset NotBefore, DateTimeOffset NotAfter);
public static class CsdCheck {
    const string SatIssuer = "SERVICIO DE ADMINISTRACION TRIBUTARIA";
    const string X500UniqueIdentifier = "2.5.4.45";
    public static CsdInfo Validate(byte[] cer, byte[] key, string password, string licensedRfc, DateTimeOffset now, bool production) {

        Need(cer.Length is > 0 and <= 8192 && key.Length is > 0 and <= 8192, "Los archivos .cer y .key del CSD pesan unos 2 KB; revisa que sean esos");
        Need(password.Length is > 0 and <= 128, "Escribe la contraseña de la llave privada del CSD");
        X509Certificate2 cert;
        try { cert = X509CertificateLoader.LoadCertificate(cer); } catch (CryptographicException) { throw new BusinessException(400, "El archivo .cer no es un certificado válido"); }
        using (cert) {

            var rfc = SubjectRfc(cert) ?? throw new BusinessException(400, "El certificado no trae RFC: no es un CSD del SAT");
            // The SAT's sandbox certificates come from its test authority; a real CSD only from the SAT.
            if (production) Need(cert.IssuerName.Name.Contains(SatIssuer, StringComparison.OrdinalIgnoreCase), "El certificado no lo emitió el SAT");
            var usage = cert.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault()?.KeyUsages ?? X509KeyUsageFlags.None;
            Need((usage & (X509KeyUsageFlags.KeyAgreement | X509KeyUsageFlags.DataEncipherment)) == 0, "Ese certificado es tu e.firma (FIEL). Para facturar se usa el Certificado de Sello Digital (CSD), que se tramita con la e.firma en CertiSAT");
            Need(usage.HasFlag(X509KeyUsageFlags.DigitalSignature), "El certificado no sirve para sellar facturas");
            var notBefore = new DateTimeOffset(cert.NotBefore.ToUniversalTime());
            var notAfter = new DateTimeOffset(cert.NotAfter.ToUniversalTime());
            Need(now >= notBefore, "El CSD empieza a valer el " + notBefore.ToString("yyyy-MM-dd"));
            Need(now < notAfter, "El CSD venció el " + notAfter.ToString("yyyy-MM-dd") + "; tramita uno nuevo en CertiSAT");
            Need(rfc == licensedRfc, "El CSD es del RFC " + rfc + " y esta sucursal está registrada con el RFC " + licensedRfc);
            using var publicKey = cert.GetRSAPublicKey() ?? throw new BusinessException(400, "El certificado no tiene llave RSA");
            using var rsa = RSA.Create();
            try { rsa.ImportEncryptedPkcs8PrivateKey(password.AsSpan(), key, out var read); Need(read == key.Length, "El archivo .key trae datos de más"); } catch (CryptographicException) { throw new BusinessException(400, "La contraseña no abre el archivo .key, o el archivo no es una llave privada del SAT"); }
            // Same modulus and a signature the certificate verifies: the .key is the one this .cer was issued for.
            var probe = RandomNumberGenerator.GetBytes(32);
            var paired = rsa.KeySize == publicKey.KeySize && publicKey.VerifyData(probe, rsa.SignData(probe, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            Need(paired, "El archivo .key no corresponde al .cer: son de sellos distintos");
            return new(rfc, Serial(cert), notBefore, notAfter);

        }

    }
    // "EKU9003173C9 / VADA800927HSRSRL05": the business RFC, then the legal representative's for a persona moral.
    static string? SubjectRfc(X509Certificate2 cert) {

        foreach (var rdn in cert.SubjectName.EnumerateRelativeDistinguishedNames()) {

            if (rdn.HasMultipleElements || rdn.GetSingleElementType().Value != X500UniqueIdentifier) continue;
            var rfc = (rdn.GetSingleElementValue() ?? "").Split('/')[0].Trim().ToUpperInvariant();
            return Sat.RfcProblem(rfc) is null && rfc is not (Sat.GenericRfc or Sat.ForeignRfc) ? rfc : null;

        }
        return null;

    }
    // The SAT's certificate number is the serial read as ASCII: 20 digits.
    static string Serial(X509Certificate2 cert) {

        var raw = Convert.FromHexString(cert.SerialNumber);
        return raw.All(b => b is >= (byte)'0' and <= (byte)'9') ? Encoding.ASCII.GetString(raw) : cert.SerialNumber;

    }
}
public static class IntakeEnvelope {
    public const string Label = "culinary-csd-v1";
    // Associated data and HKDF info: an envelope opens only for this license, this sending and this intake key.
    public static byte[] Context(string licenseId, string submissionId, string intakeSha256) => Encoding.UTF8.GetBytes(Label + "|" + licenseId + "|" + submissionId + "|" + intakeSha256);
    public static byte[] Open(ECDiffieHellman intake, JsonObject envelope, byte[] context) {

        Need(envelope.L("v") == 1, "Versión de sobre no soportada");
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(Base64Url.DecodeFromChars(envelope.S("epk")), out _);
        Need(peer.KeySize == 256, "Sobre con llave efímera inválida");
        var shared = intake.DeriveRawSecretAgreement(peer.PublicKey);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, Base64Url.DecodeFromChars(envelope.S("salt")), context);
        CryptographicOperations.ZeroMemory(shared);
        var sealedBytes = Base64Url.DecodeFromChars(envelope.S("ct"));
        Need(sealedBytes.Length > 16, "Sobre vacío");
        var plain = new byte[sealedBytes.Length - 16];
        try { using var gcm = new AesGcm(key, 16); gcm.Decrypt(Base64Url.DecodeFromChars(envelope.S("iv")), sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length), plain, context); } finally { CryptographicOperations.ZeroMemory(key); }
        return plain;

    }
    // For the tests and for documenting the format: what Culinary Dueño does with WebCrypto.
    public static JsonObject Seal(byte[] intakeSpki, byte[] plain, byte[] context) {

        using var intake = ECDiffieHellman.Create();
        intake.ImportSubjectPublicKeyInfo(intakeSpki, out _);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var salt = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(12);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, ephemeral.DeriveRawSecretAgreement(intake.PublicKey), 32, salt, context);
        var output = new byte[plain.Length + 16];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(iv, plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length), context);
        return J.O("v", 1, "epk", Base64Url.EncodeToString(ephemeral.ExportSubjectPublicKeyInfo()), "salt", Base64Url.EncodeToString(salt), "iv", Base64Url.EncodeToString(iv), "ct", Base64Url.EncodeToString(output));

    }
}
public sealed partial class BranchService {
    ICsdLoader? csdLoader; ECDiffieHellman? intakeKey; string intakeRegistered = "";
    public void UseCsdLoader(ICsdLoader loader) => csdLoader = loader;
    // A P-256 key in the license directory, readable only by the backend's user. Losing it only means registering a new
    // one; envelopes sealed to the old key expire on CulinaryServer.
    ECDiffieHellman IntakeKey() {

        if (intakeKey is not null) return intakeKey;
        var directory = licensing?.KeyDirectory ?? throw new BusinessException(503, "Las licencias no están configuradas en este servidor");
        var file = Path.Combine(directory, "fiscal-intake.p8");
        var key = ECDiffieHellman.Create();
        if (File.Exists(file)) key.ImportPkcs8PrivateKey(File.ReadAllBytes(file), out _);
        else {

            key.GenerateKey(ECCurve.NamedCurves.nistP256);
            Directory.CreateDirectory(directory);
            var temporary = file + "." + J.Id();
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            var pkcs8 = key.ExportPkcs8PrivateKey();
            using (var output = new FileStream(temporary, options)) output.Write(pkcs8);
            CryptographicOperations.ZeroMemory(pkcs8);
            File.Move(temporary, file, false);

        }
        return intakeKey = key;

    }
    // Registers the intake key once per process, then receives what the owner sent. A PAC that does not answer leaves the
    // sending pending for the next look; everything else gets a verdict.
    public async Task SyncFiscalIntake(CancellationToken ct = default) {

        if (server is null || licensing is null) return;
        var intake = IntakeKey();
        var spki = intake.ExportSubjectPublicKeyInfo();
        var sha = Convert.ToHexStringLower(SHA256.HashData(spki));
        // A CulinaryServer without these routes yet answers 404: the branch waits, like the owner's summaries do.
        try { if (intakeRegistered != sha) { await Call(HttpMethod.Put, "/branch/v1/fiscal/intake-key", J.O("spki", Base64Url.EncodeToString(spki)), ct); intakeRegistered = sha; } } catch (ServerRejection e) when (e.Status == 404) { return; }
        foreach (var item in (await Call(HttpMethod.Get, "/branch/v1/fiscal/submissions", null, ct)).A("submissions").Objects().ToArray()) {

            if (await ReceiveCsd(item, intake, sha, ct) is { } verdict) await Call(HttpMethod.Post, "/branch/v1/fiscal/submissions/" + Uri.EscapeDataString(item.S("id")) + "/result", verdict, ct);

        }

    }
    static JsonObject CsdVerdict(JsonObject csd) => J.O("status", "loaded", "rfc", csd.S("rfc"), "serial", csd.S("serial"), "notBefore", csd.S("notBefore"), "notAfter", csd.S("notAfter"), "message", "Sellos cargados con éxito");
    public async Task<JsonObject?> ReceiveCsd(JsonObject item, ECDiffieHellman intake, string intakeSha256, CancellationToken ct = default) {

        var id = item.S("id");
        JsonObject Rejected(string message) => J.O("status", "rejected", "message", message);
        var s = await View(ct);
        // Already loaded here; only the answer to CulinaryServer was lost.
        if (s.Meta.Obj("fiscalCsd").S("submission") == id) return CsdVerdict(s.Meta.Obj("fiscalCsd"));
        if (item.S("intakeSha256") != intakeSha256) return Rejected("Se cifró para otra llave de recepción de la sucursal; vuelve a enviarlo");
        var record = s.List("license").FirstOrDefault();
        var license = record is null ? null : Current(record);
        var licensedRfc = license?.Obj("fiscal").S("rfc") ?? "";
        if (licensedRfc == "") return Rejected("La licencia de esta sucursal no tiene RFC; pide a tu proveedor que capture los datos fiscales");
        if (csdLoader is null || !Domain.IsLive(s, "pac")) return Rejected("La facturación de esta sucursal no está en modo Real con Facturama; actívala antes de cargar los sellos");
        byte[] plain = [], cer = [], key = [];
        try {

            try { plain = IntakeEnvelope.Open(intake, item.Obj("envelope"), IntakeEnvelope.Context(license!.S("licenseId"), id, intakeSha256)); } catch (Exception e) when (e is CryptographicException or FormatException or BusinessException) { return Rejected("No se pudo abrir el envío; vuelve a enviarlo desde Culinary Dueño"); }
            string password;
            using (var document = JsonDocument.Parse(plain)) {

                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cer", out var c) || !root.TryGetProperty("key", out var k) || !root.TryGetProperty("password", out var pw) || c.ValueKind != JsonValueKind.String || k.ValueKind != JsonValueKind.String || pw.ValueKind != JsonValueKind.String) return Rejected("El envío no trae el .cer, el .key y la contraseña");
                cer = c.GetBytesFromBase64();
                key = k.GetBytesFromBase64();
                password = pw.GetString()!;

            }
            CsdInfo info;
            try { info = CsdCheck.Validate(cer, key, password, licensedRfc, DateTimeOffset.UtcNow, !csdLoader.IsSandbox); } catch (BusinessException e) { return Rejected(e.Message); }
            var loaded = await csdLoader.LoadCsd(cer, key, password, ct);
            if (loaded.Status == "unavailable") return null;
            if (loaded.Status != "loaded") return Rejected(loaded.Message);
            var csd = J.O("loaded", true, "rfc", info.Rfc, "serial", info.Serial, "notBefore", info.NotBefore.ToString("O"), "notAfter", info.NotAfter.ToString("O"), "loadedAt", J.Now(), "submission", id, "environment", csdLoader.IsSandbox ? "sandbox" : "production");
            await Exclusive(async () => { var current = await Load(ct); current.Meta["fiscalCsd"] = csd.DeepClone(); return await Commit(current, J.Id(), "culinary-dueno", J.Hash("fiscal-csd:" + id), J.O("loaded", true, "serial", info.Serial), "fiscal.csd.load", ct); }, ct);
            return CsdVerdict(csd);

        } catch (JsonException) { return Rejected("El envío no trae el .cer, el .key y la contraseña"); } finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(cer); CryptographicOperations.ZeroMemory(key); }

    }
}
