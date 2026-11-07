using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using MesaNexo.Core;
namespace MesaNexo.Client;

public sealed partial class MainView {
    Task UpdateDialog() {

        if (profile is null) throw new InvalidOperationException("Configura primero la conexión de esta sucursal");
        ShowModal("Actualización del cliente", Stack(Text("La sucursal busca la versión que Culinary publicó para tu licencia. Antes de instalar se comprueban la firma, la sucursal, la versión, el tamaño y la integridad del instalador.", 14, Muted), Button("Buscar actualización", async () => {

            var device = JsonNode.Parse(await vault.Read("device_" + profile.SiteId) ?? throw new InvalidOperationException("Este dispositivo todavía no fue autorizado"))!.AsObject();
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MesaNexo", "updates", J.Id());
            string? latest = null;
            var update = await Updates.Fetch(profile, device, "win-x64", directory, published: v => latest = v);
            if (update is null) { ShowModal("Actualización del cliente", Text(Updates.Verdict(latest))); return; }
            var target = Path.Combine(directory, update.S("filename"));
            ShowModal("Instalar Culinary Restaurant " + update.S("version"), Stack(Text(update.S("notes")), Text("La aplicación se cerrará y se abrirá el instalador nativo. Los datos permanecen en el servidor local.", 13, Muted), Button("Confirmar e instalar", async () => {

                var request = (JsonObject)device.DeepClone();
                request["siteId"] = profile.SiteId;
                using var http = TrustedConnection.Create(profile);
                var current = await http.PostAsJsonAsync("api/device/can-install", request);
                var allowed = JsonNode.Parse(await current.Content.ReadAsStringAsync())!;
                Rules.Need(current.IsSuccessStatusCode && allowed.B("allowed"), "La sucursal inició operación; espera a su cierre");
                Updates.Signed(update, profile.SiteId, "win-x64");
                await Updates.Verify(update, target, "win-x64");
                await App.InstallUpdate!(target);

            }, true)));

        }, true)));
        return Task.CompletedTask;

    }
}
