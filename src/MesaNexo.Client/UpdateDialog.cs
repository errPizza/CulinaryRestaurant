using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using MesaNexo.Core;
namespace MesaNexo.Client;

public sealed partial class MainView {
    Task UpdateDialog() {

        if (profile is null) throw new InvalidOperationException("Configura primero la conexión de esta sucursal");
        var bundle = Input("Ruta de publication.json");
        var package = Input("Ruta del instalador .exe");
        var publicKey = Input("Clave pública del proveedor .pem");
        var fingerprint = Input("SHA-256 de la clave pública verificado por administración");
        ShowModal("Actualización del cliente", Stack(Text("Se comprobarán firma, destinatario, versión, integridad y permisos del dispositivo. El instalador de desarrollo no tiene firma de editor de Windows.", 14, Muted), bundle, package, publicKey, fingerprint, Button("Validar y preparar instalación", async () => {

            var manifest = await Updates.Verify(bundle.Text ?? "", package.Text ?? "", publicKey.Text ?? "", fingerprint.Text ?? "", profile.SiteId, "win-x64");
            var raw = await vault.Read("device_" + profile.SiteId) ?? throw new InvalidOperationException("Este dispositivo todavía no fue autorizado");
            var device = JsonNode.Parse(raw)!.AsObject();
            device["siteId"] = profile.SiteId;
            using var http = TrustedConnection.Create(profile);
            var response = await http.PostAsJsonAsync("api/device/can-install", device);
            var permission = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Rules.Need(response.IsSuccessStatusCode && permission.B("allowed"), "Cierra sesión y todas las cuentas; verifica la cobertura y la autorización del dispositivo");
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MesaNexo", "updates", J.Id());
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, Path.GetFileName(package.Text!));
            await using (var source = File.OpenRead(package.Text!)) await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough)) { await source.CopyToAsync(output); output.Flush(true); }
            await using (var stream = File.OpenRead(target)) Rules.Need(Convert.ToHexStringLower(await SHA512.HashDataAsync(stream)) == manifest.S("sha512"), "El paquete cambió al prepararlo");
            ShowModal("Instalar Culinary Restaurant " + manifest.S("version"), Stack(Text("La aplicación se cerrará y se abrirá el instalador nativo. Los datos permanecen en el servidor local."), Button("Confirmar e instalar", async () => {

                using var confirmHttp = TrustedConnection.Create(profile);
                var current = await confirmHttp.PostAsJsonAsync("api/device/can-install", device);
                var allowed = JsonNode.Parse(await current.Content.ReadAsStringAsync())!;
                Rules.Need(current.IsSuccessStatusCode && allowed.B("allowed"), "La sucursal inició operación; espera a su cierre");
                await using (var stream = File.OpenRead(target)) Rules.Need(Convert.ToHexStringLower(await SHA512.HashDataAsync(stream)) == manifest.S("sha512"), "El instalador cambió");
                await App.InstallUpdate!(target);

            }, true)));

        }, true)));
        return Task.CompletedTask;

    }
}