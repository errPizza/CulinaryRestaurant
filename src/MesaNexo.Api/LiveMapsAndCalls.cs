using System.Text.Json.Nodes;
using MesaNexo.Core;
using MesaNexo.Integrations;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Api;
// maps.json: {apiKey, region ("mx"), language ("es"), travelMode ("DRIVE" or "TWO_WHEELER")}. The route starts at the
// restaurant's point configured on the Maps card. Restrict the key to the Geocoding and Routes APIs.
public sealed class MapsService(GoogleMaps google) {
    public static MapsService From(JsonObject o) {

        var mode = o.S("travelMode");
        if (mode == "") mode = "DRIVE";
        Need(mode is "DRIVE" or "TWO_WHEELER", "travelMode: DRIVE o TWO_WHEELER");
        var region = o.S("region");
        var language = o.S("language");
        return new(new GoogleMaps(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) }, Text(o, "apiKey", 200), region == "" ? "mx" : region, language == "" ? "es" : language, mode));

    }
    // Locating only reads from Google, so an interrupted attempt is simply asked again.
    public async Task<JobResult> Locate(BranchState s, JsonObject address, CancellationToken ct) {

        var point = await google.Geocode(address.S("text"), ct);
        if (point is null) return JobResult.Of("failed", "Google no encontró «" + address.S("text") + "»", "", ("message", "Google no encontró la dirección"));
        var (lat, lon, formatted) = point.Value;
        var fields = new List<(string, object?)> { ("lat", Math.Round(lat, 6)), ("lon", Math.Round(lon, 6)), ("formatted", formatted), ("message", "") };
        var settings = s.Related("integration", "id", "maps").FirstOrDefault()?.Obj("settings") ?? new JsonObject();
        if (settings.ContainsKey("centerLat") && settings.ContainsKey("centerLon") && await google.Route(Restaurant.Coordinate(settings, "centerLat", 90), Restaurant.Coordinate(settings, "centerLon", 180), lat, lon, ct) is { } route) {

            // Five more minutes to hand the order over, as the simulator estimates.
            fields.Add(("routeKm", route.Km));
            fields.Add(("routeMinutes", route.Minutes + 5));

        }
        return JobResult.Of("located", "", "", fields.ToArray());

    }
}
// callerid.json: {host, port (5039), username, secret}. Asterisk's AMI over TLS with a read-only user; it reconnects on its
// own and the card shows whether the switchboard is connected. Calls are recorded only with Caller ID live.
public sealed class CallerIdService(BranchService branch, AsteriskCallerId ami, ILogger<CallerIdService> log): BackgroundService {
    public static AsteriskCallerId Options(JsonObject o) { var port = o.I("port", 5039); Need(port is > 0 and < 65536, "port inválido"); return new(Text(o, "host", 200), port, Text(o, "username", 100), Text(o, "secret", 200)); }
    protected override async Task ExecuteAsync(CancellationToken ct) {

        await Task.Yield();
        var wait = 5;
        while (!ct.IsCancellationRequested) {

            try { branch.NoteIntegration("callerid", "lista"); await ami.Listen(async call => { wait = 5; await branch.RecordCaller(call, ct); }, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch (Exception e) { log.LogWarning("Conmutador sin conexión: {Kind}", e.GetType().Name); }
            branch.NoteIntegration("callerid", "sin conexión");
            try { await Task.Delay(TimeSpan.FromSeconds(wait), ct); } catch (OperationCanceledException) { break; }
            wait = Math.Min(wait * 2, 120);

        }

    }
}
public static partial class LiveServices {
    static void Maps(IServiceCollection services, IntegrationConfig config) {

        if (config.Use("maps", MapsService.From) is not { } maps) return;
        Worker(services, "maps", branch => ct => branch.SendNext("address", "maps-service", a => a.S("status") == "queued", maps.Locate, async (s, a, c) => await maps.Locate(s, a, c), ct));

    }
    static void CallerId(IServiceCollection services, IntegrationConfig config) {

        if (config.Use("callerid", CallerIdService.Options) is not { } ami) return;
        services.AddSingleton<IHostedService>(sp => new CallerIdService(sp.GetRequiredService<BranchService>(), ami, sp.GetRequiredService<ILogger<CallerIdService>>()));

    }
}
