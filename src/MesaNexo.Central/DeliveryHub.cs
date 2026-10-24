using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using MesaNexo.Integrations;
namespace MesaNexo.Central;
// Culinary integration contract v1. Not a claim of Uber/Rappi API compatibility.
public sealed class DeliveryHub(CommerceService commerce, string slug, string secret) {
    public JsonObject Receive(byte[] raw, string timestamp, string signature) {

        var e = SignedWebhooks.DeliveryV1(raw, timestamp, signature, secret);
        var provider = Rules.Text(e, "provider", 80);
        var id = Rules.Text(e, "eventId", 200);
        var p = (JsonObject)e.Obj("order").DeepClone();
        p["requestId"] = J.Hash(provider + ":" + id)[..32];
        p["clientToken"] = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(provider + ":" + id)));
        return commerce.Checkout(slug, p);

    }
}
