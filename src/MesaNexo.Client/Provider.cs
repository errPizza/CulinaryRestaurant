using System.Text.Json.Nodes;
using Avalonia.Controls;
using MesaNexo.Core;
namespace MesaNexo.Client;

public sealed partial class MainView {
    string uploadedPackage = "";
    IEnumerable<JsonObject> CentralItems(string key) => state!.A(key).Objects();
    Option[] CentralOptions(string key) => CentralItems(key).Select(x => new Option(x.S("id"), x.S("name", x.Obj("manifest").S("version", x.S("id"))))).ToArray();
    Control ProviderPage() {

        if (page == "Portafolio") return ProviderPortfolio();
        var list = Stack(Text("Administración central del proveedor · sin autoridad operativa sobre las sucursales", 14, Muted));
        if (page == "Portafolio") {

            list.Children.Add(Wrap(Button("Registrar sucursal", () => EditRestaurant(new()), true), Button("Seguridad de mi cuenta", () => Form("Cambiar contraseña del proveedor", [new("currentPassword", "Contraseña actual", "password"), new("newPassword", "Nueva contraseña", "password")], p => Act("owner.password", p)))));
            foreach (var r in CentralItems("restaurants")) list.Children.Add(Card(Stack(Text(r.S("name"), 22, Graphite, true), Text("Sitio " + r.S("siteId") + " · " + r.S("status") + " · " + r.Obj("commercial").S("mode")), Text("Despliegue: " + r.S("deployment") + " · " + r.S("distributor")), Wrap(Button("Administrar", () => EditRestaurant(r)), Button("Registrar otra sucursal", () => Form("Nueva identidad y servidor de sucursal", [new("name", "Nombre"), new("siteId", "Identidad real del nuevo servidor"), new("clientCertificateSha256", "Huella mTLS aprovisionada")], async p => { p["restaurantId"] = r.S("id"); await Act("owner.branch", p); })), Button("Exportar contratación firmada", async () => { var entitlement = await Act("owner.entitlement", J.O("id", r.S("id"))); await SaveJson("contratacion-" + r.S("siteId") + ".json", entitlement); })))));

        } else if (page == "Planes y módulos") {

            list.Children.Add(Button("Nuevo plan", () => Form("Plan comercial", [new("name", "Nombre"), new("maxUsers", "Usuarios", "int", "15"), new("maxTables", "Mesas", "int", "30"), new("maxBranches", "Sucursales", "int", "1")], p => Act("owner.plan", p))));
            foreach (var plan in CentralItems("plans")) list.Children.Add(Card(Stack(Text(plan.S("name"), 20, Graphite, true), Text(plan.L("maxUsers") + " usuarios · " + plan.L("maxTables") + " mesas · " + plan.L("maxBranches") + " sucursales · Por confirmar"))));
            foreach (var r in CentralItems("restaurants")) {

                var checks = Stack(Text(r.S("name"), 20, Graphite, true));
                var selected = r.A("modules").Strings().ToHashSet();
                var boxes = new List<CheckBox>();
                foreach (var m in Restaurant.Modules) { var c = new CheckBox { Content = Label(m), IsChecked = selected.Contains(m), Tag = m, MinHeight = 44 }; boxes.Add(c); checks.Children.Add(c); }
                checks.Children.Add(Button("Guardar módulos", () => Act("owner.modules", J.O("id", r.S("id"), "modules", boxes.Where(x => x.IsChecked == true).Select(x => x.Tag!.ToString()).ToArray())), true));
                checks.Children.Add(Text("Exporta y aplica la contratación firmada en el servidor de esa sucursal. La operación local conserva la revisión instalada durante una desconexión.", 12, Muted));
                list.Children.Add(Card(checks));

            }

        } else if (page == "Licencias y servicios") {

            foreach (var r in CentralItems("restaurants")) { var c = r.Obj("commercial"); var card = Stack(Text(r.S("name") + " · " + c.S("mode"), 22, Graphite, true), Text("Precio: Por confirmar")); foreach (var field in new[] { "usageUntil", "supportUntil", "hostingUntil", "updatesUntil" }) card.Children.Add(Text(field + ": " + c.S(field, "Sin vigencia registrada"), 13, Muted)); card.Children.Add(Text("Modificación: " + c.B("modificationRights") + " · Reventa autorizada: " + c.B("resaleAuthorized") + " · No exclusiva: " + c.B("nonExclusive"))); card.Children.Add(Button("Contratar / modificar servicio", () => Form("Coberturas separadas", [new("service", "Servicio", "select", "support", new[] { "usage", "hosting", "support", "maintenance", "updates", "resale" }.Select(x => new Option(x, x)).ToArray()), new("until", "Vigencia ISO 8601 con zona", "text", DateTimeOffset.UtcNow.AddYears(1).ToString("O")), new("enabled", "Habilitado", "bool", "true")], async p => { p["id"] = r.S("id"); await Act("owner.service", p); }))); list.Children.Add(Card(card)); }

        } else if (page == "Actualizaciones") {

            list.Children.Add(Text("Paquetes reales, firma RSA-PSS del manifiesto y publicación por destinatario. La firma de editor del instalador requiere su certificado de distribución.", 14, Muted));
            list.Children.Add(Button("Cargar paquete…", async () => { var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new() { Title = "Paquete nativo real", AllowMultiple = false }); if (files.Count == 0) return; await using var input = await files[0].OpenReadAsync(); var result = await session!.UploadPackage(files[0].Name, input); uploadedPackage = result.S("filename"); status.Text = "Paquete guardado: " + uploadedPackage; }));
            list.Children.Add(Button("Registrar release firmado", () => Form("Release", [new("filename", "Archivo cargado", "text", uploadedPackage), new("version", "Versión semántica", "text", "0.1.0"), new("platform", "Plataforma", "select", "win-x64", [new("win-x64", "Windows x64"), new("linux-x64", "Debian x64"), new("android-arm64", "Android ARM64")]), new("description", "Descripción"), new("changes", "Cambios", "multiline")], p => Act("owner.release", p)), true));
            list.Children.Add(Button("Publicar selectivamente", () => Form("Destinatarios autorizados", [new("releaseId", "Release", "select", "", CentralOptions("releases")), new("restaurantIds", "Sucursales", "multi", "", CentralOptions("restaurants"))], p => Act("owner.publish", p))));
            foreach (var release in CentralItems("releases")) list.Children.Add(Card(Stack(Text(release.Obj("manifest").S("version") + " · " + release.Obj("manifest").S("platform"), 20, Graphite, true), Text(release.S("description")), Text(release.S("changes")))));
            foreach (var publication in CentralItems("publications")) list.Children.Add(Card(Stack(Text("Publicación · " + (publication.B("active") ? "Activa" : "Retirada")), Text(string.Join(", ", publication.A("restaurantIds").Strings())), Button("Retirar publicación", () => Act("owner.withdraw", J.O("id", publication.S("id")))))));

        } else if (page == "Respaldos") {

            list.Children.Add(Button("Crear respaldo cifrado del central", () => Act("owner.backup", new()), true));
            foreach (var backup in CentralItems("backups")) list.Children.Add(Card(Stack(Text(backup.S("at") + " · " + backup.L("bytes") + " bytes"), Button("Exportar respaldo cifrado…", async () => { var file = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new() { Title = "Exportar snapshot cifrado central", SuggestedFileName = "mesa-nexo-central-" + backup.S("id") + ".sqlite" }); if (file is null) return; await using var output = await file.OpenWriteAsync(); await session!.ExportBackup(backup.S("id"), output, backup.S("sha256")); }))));
            list.Children.Add(Card(Stack(Text("Recuperación de datos", 22, Graphite, true), Text("Sucursal: usa culinaryos-snapshot.service y backup-keys/restore-keys del runtime. La interfaz y las terminales no acceden a SQLite ni a los anillos de claves."), Text("Proveedor: la base central está cifrada con su propia clave. Conserva un snapshot consistente, la clave central por separado y sus paquetes. No restaura una sucursal desde la base central."), Text("Consulta docs/RECUPERACION.md para los procedimientos y las pruebas disponibles.", 13, Muted))));

        } else if (page == "Soporte") {

            list.Children.Add(Card(Stack(Text("Soporte autorizado", 22, Graphite, true), Text("Consulta simulada de datos de negocio, solo lectura; requiere conectividad al servidor de la sucursal y consentimiento vigente. No controla el sistema operativo."), Button("Consultar con autorización", () => Form("Conexión de soporte autorizada", [new("serverUrl", "HTTPS de la sucursal"), new("siteId", "Identidad de sucursal"), new("caFile", "CA pública aprovisionada"), new("fingerprint", "SHA-256 de CA verificado"), new("grantToken", "Token de consentimiento", "password")], async p => {

                using var http = TrustedConnection.Create(new(p.S("serverUrl"), p.S("siteId"), p.S("caFile"), p.S("fingerprint")));
                var identity = JsonNode.Parse(await http.GetStringAsync("api/identity"))!;
                Rules.Need(identity.S("siteId") == p.S("siteId"), "Sucursal equivocada", 403);
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", p.S("grantToken"));
                var response = await http.GetAsync("api/support/preview");
                var result = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                Rules.Need(response.IsSuccessStatusCode, result.S("error", "Consulta no autorizada"), 403);
                var data = result.Obj("data");
                ShowModal("Consulta simulada de negocio · solo lectura", Stack(Text(data.Obj("restaurant").S("name"), 22, Graphite, true), Text(data.A("visits").Count + " cuentas visibles · " + data.A("units").Count + " unidades · " + data.A("tables").Count + " mesas"), new TextBox { Text = data.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), IsReadOnly = true, AcceptsReturn = true, MinHeight = 300 }));

            })))));
            foreach (var ticket in CentralItems("tickets")) { var row = Stack(Text(ticket.S("siteId") + " · " + ticket.S("subject"), 20, Graphite, true)); foreach (var message in ticket.A("messages").Objects()) row.Children.Add(Text(message.S("name") + ": " + message.S("message"))); row.Children.Add(Button("Responder a la sucursal", () => Form("Respuesta de soporte", [new("message", "Mensaje", "multiline")], async p => { p["id"] = ticket.S("id"); await Act("owner.ticket.reply", p); }))); foreach (var pending in CentralItems("supportOutbox").Where(x => x.S("siteId") == ticket.S("siteId") && x.S("ticketId") == ticket.S("ticketId"))) row.Children.Add(Text("Pendiente de confirmar en sucursal: " + pending.Obj("message").S("message"), 13, Muted)); list.Children.Add(Card(row)); }

        } else if (page == "Historial")
        foreach (var a in CentralItems("audit").Reverse()) list.Children.Add(Card(Stack(Text(a.S("operation"), 18, Graphite, true), Text(a.S("at") + " · " + a.S("actor"), 12, Muted))));
        return list;

    }
    Task EditRestaurant(JsonObject r) => Form("Registro comercial de sucursal", [From(r, "name", "Nombre"), From(r, "siteId", "Identidad real de sucursal"), From(r, "status", "Estado", "select", [new("active", "Activo"), new("suspended", "Suspendido")]), From(r, "planId", "Plan", "select", CentralOptions("plans")), new("mode", "Modalidad", "select", r.Obj("commercial").S("mode", "licencia"), [new("renta", "Renta"), new("licencia", "Licencia permanente"), new("codigo", "Compra de código no exclusiva")]), From(r, "deployment", "Despliegue", "select", [new("central", "Servidor local del cliente"), new("dedicated", "Dedicado"), new("distributor", "Distribuidor")]), From(r, "distributor", "Distribuidor"), From(r, "dedicatedUrl", "Destino dedicado"), From(r, "clientCertificateSha256", "Huella del certificado mTLS de esta sucursal")], async p => { if (r.S("id") != "") p["id"] = r.S("id"); await Act("owner.restaurant", p); });
    async Task SaveJson(string filename, JsonObject value) { var file = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new() { Title = "Guardar archivo", SuggestedFileName = filename }); if (file is null) return; await using var stream = await file.OpenWriteAsync(); var bytes = System.Text.Encoding.UTF8.GetBytes(value.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); await stream.WriteAsync(bytes); stream.SetLength(bytes.Length); }
}