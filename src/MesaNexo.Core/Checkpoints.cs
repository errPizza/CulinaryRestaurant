using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
namespace MesaNexo.Core;

// The audit head published outside the branch. Someone holding every local key can rewrite the chain so that it verifies,
// but cannot change what was already published: the bucket refuses overwrites and deletes, so a rewrite leaves a head that
// no longer matches. The branch runs this comparison, and so can anyone else from an export of the chain and a copy of the bucket.
public static class Checkpoints {
    public static string Prefix(string site) => site + "/checkpoints/";
    public static string AcceptedPrefix(string site) => site + "/accepted/";
    public static string AlertsPrefix(string site) => site + "/alerts/";
    // The head is in the name: a second head for the same sequence is a new object beside the first, never a replacement.
    public static string Key(JsonObject checkpoint) => $"{Prefix(checkpoint.S("siteId"))}{checkpoint.L("sequence"):D12}-{checkpoint.S("head")[..16]}.json";
    public static JsonObject Build(string site, long sequence, string head, string at, string spki, Func<byte[], byte[]> sign) {

        var checkpoint = J.O("v", 1, "siteId", site, "sequence", sequence, "head", head, "at", at, "spki", spki);
        checkpoint["signature"] = Base64Url.EncodeToString(sign(Encoding.UTF8.GetBytes(J.Canonical(checkpoint))));
        return checkpoint;

    }
    public static bool Signed(JsonObject checkpoint) {

        try {

            var copy = (JsonObject)checkpoint.DeepClone();
            var signature = Base64Url.DecodeFromChars(copy.S("signature"));
            copy.Remove("signature");
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Base64Url.DecodeFromChars(copy.S("spki")), out _);
            return key.VerifyData(Encoding.UTF8.GetBytes(J.Canonical(copy)), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        } catch (Exception e) when (e is FormatException or CryptographicException) { return false; }

    }
    // Sequence to hash, recomputed from the records; valid only if each record hashes to itself and links to the one before.
    public static Dictionary<long, string> Heads(IEnumerable<JsonObject> audit, out bool valid) {

        var heads = new Dictionary<long, string>();
        var previous = "";
        long sequence = 0;
        valid = true;
        foreach (var a in audit.Where(x => x.ContainsKey("sequence")).OrderBy(x => x.L("sequence"))) {

            var copy = (JsonObject)a.DeepClone();
            copy.Remove("hash");
            copy.Remove("kind");
            valid &= a.L("sequence") == ++sequence && a.S("previousHash") == previous && a.S("hash") == J.Hash(J.Canonical(copy));
            previous = a.S("hash");
            heads[a.L("sequence")] = previous;

        }
        return heads;

    }
    // accepted: objects a person declared as left behind by a restore. They stay in the bucket and in the result.
    public static JsonObject Compare(string site, IEnumerable<JsonObject> audit, IReadOnlyDictionary<string, JsonObject> published, IReadOnlySet<string> accepted) {

        var heads = Heads(audit, out var chain);
        var problems = new JsonArray();
        foreach (var (key, checkpoint) in published.OrderBy(x => x.Value.L("sequence")).ThenBy(x => x.Key)) {

            if (accepted.Contains(key)) continue;
            var reason = checkpoint.S("siteId") != site || !Signed(checkpoint) ? "signature" : !heads.TryGetValue(checkpoint.L("sequence"), out var local) ? "missing" : local != checkpoint.S("head") ? "different" : "";
            if (reason != "") problems.Add(J.O("key", key, "reason", reason, "sequence", checkpoint.L("sequence"), "published", checkpoint.S("head"), "local", heads.GetValueOrDefault(checkpoint.L("sequence"), "")));

        }
        var last = published.Values.MaxBy(x => x.L("sequence"));
        return J.O("valid", chain && problems.Count == 0, "chain", chain, "records", heads.Count, "checkpoints", published.Count, "accepted", accepted.Count, "lastSequence", last?.L("sequence") ?? 0, "lastAt", last?.S("at") ?? "", "problems", problems);

    }
}
