using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MesaNexo.Core;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Integrations;
// Google Geocoding (address → point) and Routes (point → driving distance and time) with a server key restricted to those
// two APIs. Both only read: repeating a call is harmless.
public sealed class GoogleMaps(HttpClient http, string apiKey, string region = "mx", string language = "es", string travelMode = "DRIVE") {
    public async Task<(decimal Lat, decimal Lon, string Formatted)?> Geocode(string address, CancellationToken ct = default) {

        Need(address.Length is > 0 and <= 500, "Dirección inválida");
        using var response = await http.GetAsync("https://maps.googleapis.com/maps/api/geocode/json?address=" + Uri.EscapeDataString(address) + "&region=" + region + "&language=" + language + "&key=" + Uri.EscapeDataString(apiKey), ct);
        response.EnsureSuccessStatusCode();
        var data = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        var status = data.S("status");
        if (status == "ZERO_RESULTS") return null;
        if (status != "OK") throw new BusinessException(status is "REQUEST_DENIED" or "INVALID_REQUEST" ? 400 : 503, "Google Geocoding respondió " + status + (data.S("error_message") is { Length: > 0 } m ? ": " + m : ""));
        var first = data.A("results").Objects().First();
        var location = first.Obj("geometry").Obj("location");
        return (location["lat"]!.GetValue<decimal>(), location["lng"]!.GetValue<decimal>(), first.S("formatted_address"));

    }
    public async Task<(decimal Km, long Minutes)?> Route(decimal fromLat, decimal fromLon, decimal toLat, decimal toLon, CancellationToken ct = default) {

        static JsonObject Point(decimal lat, decimal lon) => J.O("location", J.O("latLng", J.O("latitude", lat, "longitude", lon)));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://routes.googleapis.com/directions/v2:computeRoutes") { Content = new StringContent(J.O("origin", Point(fromLat, fromLon), "destination", Point(toLat, toLon), "travelMode", travelMode, "languageCode", language).ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Goog-Api-Key", apiKey);
        request.Headers.Add("X-Goog-FieldMask", "routes.distanceMeters,routes.duration");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new BusinessException((int)response.StatusCode >= 500 ? 503 : 400, "Google Routes respondió HTTP " + (int)response.StatusCode);
        var route = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)).A("routes").Objects().FirstOrDefault();
        if (route is null) return null;
        var seconds = long.TryParse(route.S("duration").TrimEnd('s'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        return (Math.Round(route.L("distanceMeters") / 1000m, 1), (long)Math.Ceiling(seconds / 60.0));

    }
}
