using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MesaNexo.Core;
namespace MesaNexo.Client;

public sealed partial class MainView {
    int layoutBucket = -1; string selectedTable = ""; Grid? accountDrawer; string lastAnimatedPage = "", lastAnimatedDrawer = "";
    double ViewWidth => Bounds.Width > 0 ? Bounds.Width : 1360;
    static readonly IBrush Topbar = Brush.Parse("#202733"), Raised = Brush.Parse("#273241"), Pale = Brush.Parse("#C6D0DF");
    static TextBlock Eyebrow(string value, IBrush? color = null) => new() { Text = value, FontFamily = Brand.InterfaceFont, FontSize = 11, LetterSpacing = 1.35, FontWeight = FontWeight.SemiBold, Foreground = color ?? Muted, TextWrapping = TextWrapping.Wrap };
    static StackPanel Horizontal(params Control[] children) { var row = Stack(children); row.Orientation = Orientation.Horizontal; row.Spacing = 8; row.VerticalAlignment = VerticalAlignment.Center; return row; }
    static Border Badge(string value, string? label = null) {

        var colors = value switch { "ready" => ("#DEF5E8", "#07653F"), "occupied" or "preparing" or "received" or "confirmed" => ("#E8F0FF", "#2059A2"), "reserved" => ("#F0E7F7", "#76508B"), "cleaning" or "held" or "pending" or "proposed" or "bill" or "printed" => ("#FFF0C4", "#765000"), "delivered" or "active" or "approved" => ("#E2F2E8", "#16633E"), "delayed" or "unavailable" or "cancelled" or "rejected" => ("#FFEBE4", "#AF2D22"), _ => ("#E9EDF2", "#536173") };
        return new Border { Background = Brush.Parse(colors.Item1), CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 5), HorizontalAlignment = HorizontalAlignment.Left, Child = Text(label ?? Label(value), 11, Brush.Parse(colors.Item2), true) };

    }
    Button TextButton(string label, Func<Task> action) { var b = Button(label, action); b.Classes.Add("text"); b.Background = Brushes.Transparent; b.BorderThickness = new Thickness(0); b.Foreground = Brush.Parse("#775300"); b.FontSize = 12; b.Padding = new Thickness(7); return b; }
    string GroupName() => user?.S("role") == "cocina" ? "Preparación" : page switch { "Catálogo" or "Caja" or "Reportes" or "Equipo y dispositivos" or "Promociones" => "Administración", "Reservaciones" or "Evaluaciones" or "Restaurante" or "Soporte" => "Restaurante", _ => "Servicio" };
    static string PageIcon(string value) => value switch { "Resumen del turno" => "grid", "Mesas y cuentas" or "Cocina en vivo" => "food", "Bandeja de pendientes" => "bell", "Catálogo" or "Historial de preparación" => "book", "Caja" => "wallet", "Equipo y dispositivos" => "users", "Reservaciones" or "Promociones" => "calendar", "Soporte" => "help", "Restaurante" or "Planes y módulos" => "settings", "Actualizaciones" => "cloud", _ => "chart" };
    Task Navigate(string name) { page = name; visit = ""; Render(); return Task.CompletedTask; }
    async Task Logout() {

        if (await session!.HasPending()) throw new InvalidOperationException("Reconcilia la operación incierta antes de cerrar sesión");
        await session.Execute("session.logout", new());
        await session.StopLive();
        timer.Stop();
        session.Dispose();
        session = null;
        state = null;
        user = null;
        visit = "";
        if (accountDrawer is not null) { shell.Children.Remove(accountDrawer); accountDrawer = null; }
        ShowLogin();

    }
    Button NavigationButton(string name) {

        var selected = page == name;
        var b = Button(name, () => Navigate(name), selected);
        b.Classes.Add("nav");
        b.Background = selected ? Amber : Brushes.Transparent;
        b.Foreground = selected ? Brush.Parse("#29210C") : Brush.Parse("#CED7E4");
        b.BorderThickness = new Thickness(0);
        b.Padding = new Thickness(12, 10);
        b.FontSize = ViewWidth <= 650 ? 11 : 12;
        var icon = Brand.Icon(PageIcon(name), b.Foreground, ViewWidth <= 650 ? 17 : 20);
        var title = Text(name, b.FontSize, b.Foreground, selected);
        var body = Horizontal(icon, title);
        body.Spacing = 8;
        if (name == "Bandeja de pendientes" && Items("notification").Count(x => !x.B("attended")) is int count && count > 0) body.Children.Add(Badge("pending", count.ToString()));
        b.Content = body;
        return b;

    }
    void Header() {

        foreach (var old in shell.Children.Where(c => Grid.GetRow(c) == 0 && c != content && c != status && c != modal && c != accountDrawer).ToArray()) shell.Children.Remove(old);
        if (user is null) return;
        var mobile = ViewWidth <= 650;
        var brandSize = mobile ? 34 : 40;
        var identity = Stack(Horizontal(Brand.Image(false, brandSize, brandSize), Text(Brand.Name, mobile ? 16 : 19, Brushes.White, true)), new Border { BorderBrush = Amber, BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(7, 0, 0, 0), Margin = new Thickness(brandSize + 10, 0, 0, 0), Child = Text(user.S("role") == "owner" ? "Panel del propietario" : state?.Obj("restaurant").S("name") ?? "", mobile ? 10 : 11, Pale) });
        identity.Spacing = 5;
        var profileButton = Button(user.S("name") + " · Cerrar sesión", Logout);
        profileButton.Classes.Add("nav");
        profileButton.Background = Brushes.Transparent;
        profileButton.BorderThickness = new Thickness(0);
        profileButton.Padding = new Thickness(0, 3);
        var avatar = new Border { Width = mobile ? 29 : 33, Height = mobile ? 29 : 33, CornerRadius = new CornerRadius(18), Background = Brush.Parse("#394658"), Child = new TextBlock { Text = user.S("name").FirstOrDefault().ToString(), Foreground = Brush.Parse("#F7DB9F"), FontSize = 14, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var profileBody = Horizontal(avatar);
        if (!mobile) { var details = Stack(Text(user.S("name"), 12, Brushes.White, true), Text(Label(user.S("role")) + " · Cerrar sesión", 10, Pale)); details.Spacing = 2; details.MaxWidth = 110; foreach (var text in details.Children.OfType<TextBlock>()) { text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis; } profileBody.Children.Add(details); }
        profileButton.Content = profileBody;
        var version = TextButton("Culinary Restaurant 0.2.0", About);
        version.Classes.Add("nav");
        version.Content = Horizontal(Text(mobile ? "0.2.0" : "0.2.0", 10, Pale), Brand.Icon("cloud", Pale, 14));
        version.Foreground = Pale;
        version.Padding = new Thickness(10, 8);
        version.BorderThickness = new Thickness(1, 0, 0, 0);
        version.BorderBrush = Brush.Parse("#4A5567");
        var right = Horizontal(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Brush.Parse("#46BE82") }, profileButton, version);
        right.Spacing = mobile ? 8 : 12;
        var role = user.S("role");
        var quick = role == "dueno" ? new[] { "Analítica" } : role == "cajero" ? new[] { "Arqueo ciego" } : role == "cocina" ? new[] { "Cocina en vivo", "Historial de preparación" } : role == "manager" ? new[] { "Resumen del turno", "Mesas y cuentas", "Bandeja de pendientes" } : new[] { "Resumen del turno", "Mesas y cuentas", "Bandeja de pendientes" };
        var nav = Stack();
        nav.Spacing = 7;
        Control quickView;
        if (mobile) { var grid = new UniformGrid { Columns = 2, Rows = 0 }; foreach (var name in quick) { var b = NavigationButton(name); b.Margin = new Thickness(0, 0, 6, 6); b.HorizontalAlignment = HorizontalAlignment.Stretch; grid.Children.Add(b); } quickView = grid; } else { var row = Horizontal(quick.Select(NavigationButton).Cast<Control>().ToArray()); row.Spacing = 6; quickView = row; }
        var groups = new List<(string Name, string Icon, string[] Pages)>();
        if (role == "manager") {

            groups.Add(("Administración", "chart", ["Cocina en vivo", "Historial de preparación", "Catálogo", "Caja", "Inventario", "Delivery y clientes", "Impresión", "Fiscal", "Reportes", "Equipo y dispositivos", .. (state!.Obj("restaurant").A("modules").Strings().Contains("promotions") ? new[] { "Promociones" } : Array.Empty<string>())]));
            var restaurant = new List<string>();
            if (state!.Obj("restaurant").A("modules").Strings().Contains("reservations")) restaurant.Add("Reservaciones");
            if (state.Obj("restaurant").A("modules").Strings().Contains("reviews")) restaurant.Add("Evaluaciones");
            restaurant.AddRange(["Restaurante", "Soporte"]);
            groups.Add(("Restaurante", "settings", restaurant.ToArray()));

        } else if (role == "mesero" && user.Obj("permissions").B("shiftReports", true)) groups.Add(("Administración", "chart", ["Reportes"]));
        var menus = Horizontal();
        menus.Spacing = 6;
        foreach (var group in groups) { var selected = group.Pages.Contains(page); var button = Button(group.Name, () => Task.CompletedTask, selected); button.Classes.Add("nav"); button.Background = selected ? Amber : Brushes.Transparent; button.BorderThickness = new Thickness(0); button.FontSize = 12; button.Content = Horizontal(Brand.Icon(group.Icon, selected ? Graphite : Pale, 18), Text(group.Name, 12, selected ? Graphite : Pale), Brand.Icon("chevron-down", selected ? Graphite : Pale, 14)); var menu = new MenuFlyout(); foreach (var name in group.Pages) { var item = new MenuItem { Header = name, Icon = Brand.Icon(PageIcon(name)), MinHeight = 44 }; item.Click += async (_, _) => await Run(() => Navigate(name)); menu.Items.Add(item); } button.Flyout = menu; menus.Children.Add(button); }
        if (mobile) { nav.Children.Add(quickView); if (groups.Count > 0) nav.Children.Add(menus); } else { var horizontal = Horizontal(quickView, menus); horizontal.Spacing = 6; nav.Children.Add(horizontal); }
        var twoRows = ViewWidth < 1360;
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions(twoRows ? "*,Auto" : "Auto,*,Auto"), RowDefinitions = new RowDefinitions(twoRows ? "Auto,Auto" : "Auto") };
        headerGrid.Children.Add(identity);
        Grid.SetColumn(right, twoRows ? 1 : 2);
        headerGrid.Children.Add(right);
        if (twoRows) { Grid.SetRow(nav, 1); Grid.SetColumnSpan(nav, 2); nav.Margin = new Thickness(0, 14, 0, 0); headerGrid.Children.Add(new Border { BorderBrush = Brush.Parse("#3E495A"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 13, 0, 0), Child = nav }); var wrapper = headerGrid.Children.Last(); Grid.SetRow(wrapper, 1); Grid.SetColumnSpan(wrapper, 2); nav.Margin = new Thickness(0, 10, 0, 0); } else { Grid.SetColumn(nav, 1); nav.Margin = new Thickness(24, 0, 15, 0); headerGrid.Children.Add(nav); }
        var header = new Border { Background = Topbar, BorderBrush = Brush.Parse("#3B4656"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(mobile ? 16 : ViewWidth <= 1200 ? 24 : 30, mobile ? 13 : 17), Child = headerGrid };
        shell.Children.Add(header);

    }
    Task About() {

        var image = Brand.Image(true, 360, 216);
        image.HorizontalAlignment = HorizontalAlignment.Center;
        var rows = Stack(image, Text(Brand.Name, 22, Graphite, true), Text("Versión 0.2.0", 13, Muted), Text("Aplicación interna · Datos persistentes", 13, Muted));
        var reduced = new CheckBox { Content = "Reducir movimiento", IsChecked = PrototypeMotion.ReduceMotion, MinHeight = 44 };
        reduced.IsCheckedChanged += async (_, _) => { PrototypeMotion.ReduceMotion = reduced.IsChecked == true; await vault.Write("motion.reduced", PrototypeMotion.ReduceMotion ? "true" : "false"); };
        rows.Children.Add(reduced);
        if (App.InstallUpdate is not null) rows.Children.Add(Button("Instalar actualización firmada…", UpdateDialog));
        ShowModal("Software y actualizaciones", rows);
        return Task.CompletedTask;

    }
    Task ConnectionDialog() {

        var rows = Stack(Text(profile?.ServerUrl ?? "Servidor aprovisionado", 16, Graphite, true), Text("Sucursal: " + (profile?.SiteId ?? state?.Obj("restaurant").S("siteId")), 13, Muted), Text(status.Text ?? "", 13, Muted));
        if (session is not null) { rows.Children.Add(Button("Reconciliar / reintentar operación pendiente", async () => { await session.Reconcile(true); state = await session.State(); CloseModal(); Render(); })); rows.Children.Add(Button("Cerrar sesión y configurar dirección", async () => { await Logout(); CloseModal(); })); }
        ShowModal("Conexión al backend", rows);
        return Task.CompletedTask;

    }
    void ShowLogin() {

        Header();
        if (accountDrawer is not null) { shell.Children.Remove(accountDrawer); accountDrawer = null; }
        var url = Input("Dirección HTTPS", profile?.ServerUrl ?? "https://culinaryos.internal:8443");
        var site = Input("Identidad de sucursal", profile?.SiteId ?? "");
        var ca = Input("Archivo de CA pública", profile?.CaFile ?? "");
        var fingerprint = Input("Huella SHA-256 verificada", profile?.CaSha256 ?? "");
        var enrollment = Input("Código de alta (solo dispositivo nuevo)", password: true);
        var tenant = Input("Empresa (sólo analítica del dueño)", profile?.TenantId ?? "");
        var branchId = Input("Sucursal del dueño", profile?.BranchId ?? "");
        var username = Input("Usuario");
        var password = Input("Contraseña", password: true);
        Task Configure() {

            var fields = Stack(Text("La CA y su huella se entregan por administración. El descubrimiento propone candidatos; no concede confianza.", 12, Muted));
            foreach (var pair in new[] { ("Dirección HTTPS", url), ("Identidad de sucursal", site), ("Archivo de CA pública", ca), ("Huella SHA-256 verificada", fingerprint), ("Código de alta", enrollment), ("Empresa del dueño", tenant), ("Sucursal del dueño", branchId) }) fields.Children.Add(Stack(Text(pair.Item1, 13, Graphite, true), pair.Item2));
            fields.Children.Add(Button("Elegir certificado público…", async () => { var top = TopLevel.GetTopLevel(this); if (top?.StorageProvider is null) return; var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = "CA pública verificada", AllowMultiple = false }); if (files.Count == 0) return; var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MesaNexo", "public-ca"); Directory.CreateDirectory(directory); var target = Path.Combine(directory, J.Id() + ".crt"); await using var input = await files[0].OpenReadAsync(); await using var output = File.Create(target); await input.CopyToAsync(output); ca.Text = target; }));
            if (App.Discovery is not null) fields.Children.Add(Button("Buscar servidor en la red local", async () => { var candidates = await App.Discovery.Browse(); var rows = Stack(Text("Verifica el nombre y la CA aprovisionados antes de elegir.")); foreach (var found in candidates) { var c = found; rows.Children.Add(Button(c.DisplayName + " · " + c.Address + ":" + c.Port, async () => { var uri = new Uri(url.Text ?? ""); Rules.Need(uri.Port == c.Port, "El puerto no coincide con el aprovisionado"); var proposed = new ConnectionProfile(url.Text ?? "", site.Text ?? "", ca.Text ?? "", fingerprint.Text ?? "", c.Address); using var verify = TrustedConnection.Create(proposed); var identity = JsonNode.Parse(await verify.GetStringAsync("api/identity"))!; Rules.Need(identity.S("siteId") == proposed.SiteId, "El servidor pertenece a otra sucursal", 403); profile = proposed; CloseModal(); await Configure(); })); } if (candidates.Count == 0) rows.Children.Add(Text("Sin candidatos; usa la dirección manual.")); ShowModal("Servidores de red", rows); }));
            fields.Children.Add(Button("Guardar", async () => { var proposed = new ConnectionProfile(url.Text?.Trim() ?? "", site.Text?.Trim() ?? "", ca.Text?.Trim() ?? "", fingerprint.Text?.Trim() ?? "", profile?.ConnectAddress, tenant.Text?.Trim() ?? "", branchId.Text?.Trim() ?? ""); using var check = TrustedConnection.Create(proposed); var identity = JsonNode.Parse(await check.GetStringAsync("api/identity"))!; Rules.Need(identity.S("siteId") == proposed.SiteId, "El servidor pertenece a otra sucursal", 403); profile = proposed; await vault.Write("profile", JsonSerializer.Serialize(profile)); CloseModal(); }, true));
            ShowModal("Conexión al backend", fields);
            return Task.CompletedTask;

        }
        var login = Button("Entrar a la operación", async () => { profile = new ConnectionProfile(url.Text?.Trim() ?? "", site.Text?.Trim() ?? "", ca.Text?.Trim() ?? "", fingerprint.Text?.Trim() ?? "", profile?.ServerUrl == url.Text?.Trim() ? profile?.ConnectAddress : null, tenant.Text?.Trim() ?? "", branchId.Text?.Trim() ?? ""); session?.Dispose(); session = new NativeSession(profile, vault); var result = await session.Login(username.Text ?? "", password.Text ?? "", Environment.MachineName, enrollment.Text ?? ""); password.Text = ""; enrollment.Text = ""; user = result.Obj("user"); state = await session.State(); page = user.S("role") == "dueno" ? "Analítica" : user.S("role") == "cajero" ? "Arqueo ciego" : user.S("role") == "cocina" ? "Cocina en vivo" : "Resumen del turno"; await vault.Write("profile", JsonSerializer.Serialize(profile)); Render(); timer.Start(); try { await session.StartLive(async () => await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RefreshLive)); } catch { } }, true);
        login.HorizontalAlignment = HorizontalAlignment.Stretch;
        login.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        login.Content = Horizontal(Text("Entrar a la operación", 13, Brush.Parse("#29210C"), true), Brand.Icon("arrow-right", Graphite, 17));
        AutomationProperties.SetName(login, "Entrar a la operación");
        var fieldsLogin = Stack(Eyebrow("BIENVENIDO A TU TURNO"), Text("Inicia sesión", 34, Raised, true), Text("Usa tu cuenta del restaurante.", 13, Muted));
        fieldsLogin.Spacing = 15;
        fieldsLogin.Children.Add(Stack(Text("Usuario", 13, Graphite, true), username));
        fieldsLogin.Children.Add(Stack(Text("Contraseña", 13, Graphite, true), password));
        fieldsLogin.Children.Add(login);
        fieldsLogin.Children.Add(TextButton("Configurar dirección del backend", Configure));
        fieldsLogin.Children.Add(TextButton("Ver último catálogo guardado", async () => { var raw = await vault.Read("last_catalog_" + (site.Text?.Trim() ?? "")); Rules.Need(raw is not null, "Este dispositivo aún no tiene un catálogo guardado"); var cached = JsonNode.Parse(raw!)!; var rows = Stack(Text("Catálogo guardado · sólo consulta", 20, Graphite, true), Text("Actualizado " + DateLabel(cached.S("at")) + ". Inicia sesión con el servidor local para confirmar pedidos.", 12, Muted)); foreach (var p in cached.A("products").Objects().Where(x => x.B("visible"))) rows.Children.Add(Text(p.S("name") + " · " + Money(p.L("price")))); ShowModal("Catálogo local", rows); }));
        fieldsLogin.Children.Add(Text("Versión 0.2.0 · " + (profile?.ServerUrl ?? "Configura la conexión"), 11, Muted));
        fieldsLogin.MaxWidth = 380;
        fieldsLogin.Width = ViewWidth <= 650 ? Math.Max(0, ViewWidth - 48) : 380;
        fieldsLogin.HorizontalAlignment = HorizontalAlignment.Center;
        fieldsLogin.VerticalAlignment = VerticalAlignment.Center;
        var story = Stack(Horizontal(Brand.Image(false, 42, 42), Text(Brand.Name, 22, Brushes.White, true)), new Border { Height = ViewWidth <= 650 ? 10 : 60 }, Eyebrow("CADA MESA, BIEN ATENDIDA", Brush.Parse("#EDCB7E")), Text("La operación fluye.\nTu equipo también.", ViewWidth <= 650 ? 29 : 44, Brushes.White, true), Text("Mesas, cocina y administración\nen un mismo lugar.", 16, Pale));
        story.Spacing = 18;
        if (ViewWidth > 650) { story.Children.Add(new Border { Height = 248, MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 15, 0, 0) }); story.Children.Add(Text("Recibido → Preparado → Entregado", 12, Brush.Parse("#EDCB7E"))); }
        var storyBox = new Border { Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#334258"), 0), new GradientStop(Color.Parse("#202733"), 1) } }, Padding = new Thickness(ViewWidth <= 650 ? 24 : 48), Child = story };
        var formBox = new Border { Padding = new Thickness(24, 35), MinHeight = ViewWidth <= 650 ? 480 : 700, Background = Brush.Parse("#F9FAFC"), Child = fieldsLogin };
        Control layout;
        if (ViewWidth <= 650) { layout = Stack(storyBox, formBox); ((StackPanel)layout).Spacing = 0; } else { var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), MinHeight = Math.Max(700, Bounds.Height - 40) }; grid.Children.Add(storyBox); Grid.SetColumn(formBox, 1); grid.Children.Add(formBox); layout = grid; }
        content.Content = new ScrollViewer { Content = layout, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        status.FontSize = 10;
        Status();
        _ = Restore();
        async Task Restore() { try { if (profile is null && await vault.Read("profile") is string raw) { profile = JsonSerializer.Deserialize<ConnectionProfile>(raw); url.Text = profile?.ServerUrl; site.Text = profile?.SiteId; ca.Text = profile?.CaFile; fingerprint.Text = profile?.CaSha256; tenant.Text = profile?.TenantId; branchId.Text = profile?.BranchId; } } catch (Exception e) { status.Text = e.Message; } }

    }
    void Render() {

        if (state is null || user is null) return;
        clockRefresh.Clear();
        Header();
        if (accountDrawer is not null) { shell.Children.Remove(accountDrawer); accountDrawer = null; }
        var title = Stack(Eyebrow(state.Obj("restaurant").S("name").ToUpperInvariant() + " / " + GroupName().ToUpperInvariant()), Text(page, ViewWidth <= 650 ? 27 : ViewWidth <= 900 ? 28 : 31, Graphite, true));
        title.Spacing = 9;
        var tools = Horizontal();
        { var branch = new ComboBox { ItemsSource = new[] { state.Obj("restaurant").S("branchName", "Sucursal Centro") }, SelectedIndex = 0, MinHeight = 40, Width = ViewWidth <= 380 ? 110 : 160, FontSize = 12 }; AutomationProperties.SetName(branch, "Sucursal"); tools.Children.Add(branch); }
        var connection = Button("Conexión", ConnectionDialog);
        connection.FontSize = 11;
        connection.Padding = new Thickness(10, 9);
        connection.Content = Horizontal(Brand.Icon("settings", null, 17), Text("Conexión", 11));
        tools.Children.Add(connection);
        tools.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(9), BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(13, 10), Child = Text(DateTime.Now.ToString("d MMM", CultureInfo.GetCultureInfo("es-MX")), 12, Muted), BoxShadow = BoxShadows.Parse("0 3 0 0 #CDD3DC") });
        Control heading;
        if (ViewWidth <= 650) { heading = Stack(title, tools); ((StackPanel)heading).Spacing = 15; } else { var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") }; grid.Children.Add(title); Grid.SetColumn(tools, 1); grid.Children.Add(tools); heading = grid; }
        var body = Stack(heading);
        body.Spacing = 27;
        body.Margin = new Thickness(ViewWidth <= 380 ? 13 : ViewWidth <= 650 ? 16 : ViewWidth <= 900 ? 25 : ViewWidth <= 1200 ? 27 : ViewWidth >= 1600 ? 42 : 34, ViewWidth <= 650 ? 23 : 30, ViewWidth <= 650 ? 16 : 34, 14);
        body.MaxWidth = 1660;
        body.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (error != "") body.Children.Add(new Border { Background = Brush.Parse("#FFF0E9"), BorderBrush = Brush.Parse("#E8BFAE"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Padding = new Thickness(18, 13), Child = Text(error, 12, Brush.Parse("#A33424")) });
        if (session?.Connection == ConnectionState.Uncertain) body.Children.Add(Button("Reconciliar / reintentar operación pendiente", async () => { await session.Reconcile(true); state = await session.State(); Render(); }));
        Control view = page switch { "Analítica" => OwnerAnalytics(), "Resumen del turno" => Overview(), "Mesas y cuentas" => Tables(), "Cocina en vivo" => Kitchen(false), "Historial de preparación" => Kitchen(true), "Bandeja de pendientes" => Notices(), "Catálogo" => Catalog(), "Caja" => Cash(), "Arqueo ciego" => BlindCash(), "Inventario" => InventoryPage(), "Delivery y clientes" => DeliveryPage(), "Impresión" => PrintJobsPage(), "Fiscal" => FiscalPage(), "Reportes" => Reports(), "Equipo y dispositivos" => Team(), "Restaurante" => Settings(), "Reservaciones" => Reservations(), "Evaluaciones" => Reviews(), "Promociones" => Promotions(), "Soporte" => Support(), "Historial" => Audit(), _ => Text("Selecciona una pantalla") };
        body.Children.Add(view);
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4, 0, 0) };
        footer.Children.Add(Text(Brand.Name + " · " + "Aplicación interna", 10, Muted));
        var stamp = Text("Datos persistentes · Versión 0.2.0", 10, Muted);
        if (ViewWidth > 500) { Grid.SetColumn(stamp, 1); footer.Children.Add(stamp); }
        body.Children.Add(new Border { BorderBrush = Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 22, 0, 0), Child = footer });
        content.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        if (lastAnimatedPage != page) { PrototypeMotion.Enter(view); lastAnimatedPage = page; }
        if (visit == "") lastAnimatedDrawer = "";
        status.FontSize = 10;
        status.Margin = new Thickness(ViewWidth <= 650 ? 16 : 34, 6);
        Status();
        if (page == "Mesas y cuentas" && visit != "") ShowAccountDrawer();

    }
    void ShowAccountDrawer() {

        var v = Item("visit", visit);
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 18) };
        heading.Children.Add(Wrap(Eyebrow("CUENTA EN SERVICIO"), Badge(v.S("status")), Text("Cuenta " + visit[..Math.Min(8, visit.Length)], 11, Muted)));
        var close = Button("×", () => { visit = ""; Render(); return Task.CompletedTask; });
        AutomationProperties.SetName(close, "Cerrar cuenta lateral");
        Grid.SetColumn(close, 1);
        heading.Children.Add(close);
        var body = Stack(heading, Account());
        KeyboardNavigation.SetTabNavigation(body, KeyboardNavigationMode.Cycle);
        var surface = new Border { Background = Brush.Parse("#EFF1F5"), Width = ViewWidth <= 650 ? ViewWidth : Math.Min(1080, ViewWidth - 120), HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(ViewWidth <= 650 ? 17 : 28, 25), CornerRadius = new CornerRadius(ViewWidth <= 650 ? 0 : 19, 0, 0, ViewWidth <= 650 ? 0 : 19), Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        accountDrawer = new Grid { Background = Brush.Parse("#5C101C35"), ZIndex = 60 };
        accountDrawer.Children.Add(surface);
        if (lastAnimatedDrawer != visit) { PrototypeMotion.Enter(surface, 320, surface.Width, 0); PrototypeMotion.Enter(accountDrawer, 180, 0, 0); lastAnimatedDrawer = visit; }
        Grid.SetRowSpan(accountDrawer, 3);
        shell.Children.Add(accountDrawer);
        accountDrawer.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape && modal is null) { visit = ""; Render(); e.Handled = true; } }, RoutingStrategies.Tunnel);
        AutomationProperties.SetName(accountDrawer, "Cuenta de " + EntityName("table", Item("visit", visit).S("tableId")));

    }
}