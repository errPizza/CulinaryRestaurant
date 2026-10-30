using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MesaNexo.Core;
namespace MesaNexo.Client;

// Shown instead of everything else while the branch server refuses with 423: the license is missing, expired,
// cancelled, unvalidated for 7 days, or the server's clock was set back. It shows no business data.
public sealed partial class MainView {
    // While set, resizing redraws the lock screen instead of the sign-in form.
    JsonObject? lockedLicense;
    void LeaveLock() { lockedLicense = null; ShowLogin(); }
    async Task ShowLicenseLock() {

        timer.Stop();
        session?.Dispose();
        session = null;
        user = null;
        state = null;
        JsonObject license;
        try { using var http = TrustedConnection.Create(profile!); license = JsonNode.Parse(await http.GetStringAsync("api/license")) as JsonObject ?? new(); } catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException) { license = J.O("state", "unknown"); }
        RenderLicenseLock(license);

    }
    void RenderLicenseLock(JsonObject license) {

        lockedLicense = license;
        Header();
        var (title, body) = LicenseCopy(license);
        var panel = Stack(Eyebrow("LICENCIA DE USO"), Text(title, 30, Raised, true));
        panel.Spacing = 14;
        if (license.S("plan") != "") panel.Children.Add(Text(LicenseLine(license), 15, Graphite, true));
        panel.Children.Add(Text(body, 14, Muted));
        panel.Children.Add(Wrap(Button("Reintentar", async () => { if (await Usable()) LeaveLock(); else await ShowLicenseLock(); }, true), Button("Activar licencia", () => Form("Activar Culinary Restaurant", [new("licenseId", "Identificador de licencia"), new("activationCode", "Código de activación")], async p => {

            using var http = TrustedConnection.Create(profile!);
            using var response = await http.PostAsJsonAsync("api/license/activate", J.O("licenseId", p.S("licenseId"), "activationCode", p.S("activationCode")));
            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync()) as JsonObject ?? new();
            Rules.Need(response.IsSuccessStatusCode, reply.S("error", "No se pudo activar la licencia"), (int)response.StatusCode);
            LeaveLock();

        }))));
        panel.MaxWidth = 520;
        panel.HorizontalAlignment = HorizontalAlignment.Center;
        panel.VerticalAlignment = VerticalAlignment.Center;
        var card = Card(panel);
        card.Padding = new Thickness(36);
        card.MaxWidth = 600;
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        content.Content = new ScrollViewer { Content = new Border { Padding = new Thickness(ViewWidth <= 650 ? 16 : 48), MinHeight = Math.Max(480, Bounds.Height - 60), Background = Brush.Parse("#F9FAFC"), Child = card } };
        AutomationProperties.SetName(card, title);

    }
    async Task<bool> Usable() { using var http = TrustedConnection.Create(profile!); return (JsonNode.Parse(await http.GetStringAsync("api/license")))!.S("state") == "active"; }
    static string PlanName(string plan) => plan switch { "trial" => "Prueba", "monthly" => "Mensual", "annual" => "Anual", "permanent" => "Permanente", _ => plan };
    string LicenseLine(JsonObject l) => "Plan " + PlanName(l.S("plan")) + (l["expiresAt"] is JsonValue ? " · " + (l.S("state") == "expired" ? "venció el " : "vence el ") + DateLabel(l.S("expiresAt")) : "");
    // Same wording as the web lock screen (license-lock.tsx).
    static (string Title, string Body) LicenseCopy(JsonObject l) => l.S("state") switch {
        "expired" when l.S("plan") == "trial" => ("Tu periodo de prueba terminó", "Tus datos siguen guardados en este equipo. Para entrar de nuevo al panel de administración y a la operación, contrata un plan con tu proveedor. Al activarlo, el acceso vuelve solo."),
        "expired" => ("Tu licencia venció", "Tus datos siguen guardados en este equipo. Para entrar de nuevo al panel de administración y a la operación, contrata otra vez tu plan con tu proveedor. Al renovarlo, el acceso vuelve solo."),
        "revoked" => ("Tu licencia fue cancelada", "Tus datos siguen guardados en este equipo. Comunícate con tu proveedor para contratar una licencia nueva y activarla aquí."),
        "offline" => ("No pudimos validar tu licencia", "Culinary Restaurant necesita conectarse a internet al menos una vez cada 7 días. Revisa la conexión del servidor de la sucursal y reintenta."),
        "clock" => ("La fecha del equipo está atrasada", "Corrige la fecha y la hora del servidor de la sucursal y reintenta."),
        "mismatch" => ("Esta licencia es de otro equipo", "Pide a tu proveedor que libere la licencia y actívala de nuevo en este servidor."),
        "missing" => ("Activa Culinary Restaurant", "Escribe el identificador de licencia y el código de activación que te dio tu proveedor."),
        _ => ("No se pudo consultar la licencia", "Revisa la conexión con el servidor de la sucursal y reintenta.")
    };
}
