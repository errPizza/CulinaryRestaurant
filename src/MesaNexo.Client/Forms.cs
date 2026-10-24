using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MesaNexo.Core;
namespace MesaNexo.Client;

public sealed partial class MainView {
    public record Option(string Id, string Name) { public override string ToString() => Name; }
    public record Field(string Key, string Label, string Type = "text", string Initial = "", Option[]? Options = null);
    Option[] Options(string kind, Func<JsonObject, bool>? filter = null, bool empty = false) {

        var options = Items(kind).Where(x => filter?.Invoke(x) ?? true).Select(x => new Option(x.S("id"), x.S("name", x.S("id")))).ToList();
        if (empty) options.Insert(0, new("", "Sin cambio"));
        return options.ToArray();

    }
    static Field From(JsonObject p, string key, string label, string type = "text", Option[]? options = null) => new(key, label, type, type == "bool" ? p.B(key).ToString().ToLowerInvariant() : type == "money" ? (p.L(key) / 100m).ToString(CultureInfo.InvariantCulture) : p[key]?.ToString() ?? "", options);
    Task Form(string title, Field[] fields, Func<JsonObject, Task> accept) {

        var rows = Stack();
        if (title == "Registrar pago") rows.Children.Add(Text("Solo registra un pago recibido; no procesa tarjetas ni transferencias.", 12, Muted));
        var readers = new Dictionary<string, Func<JsonNode?>>();
        foreach (var f in fields) {

            Control control;
            if (f.Type == "bool") {

                var check = new CheckBox { Content = f.Label, IsChecked = f.Initial == "true", MinHeight = 44 };
                control = check;
                readers.Add(f.Key, () => J.N(check.IsChecked == true));

            } else if (f.Type == "select") {

                var select = new ComboBox { ItemsSource = f.Options ?? [], MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
                select.SelectedItem = f.Options?.FirstOrDefault(x => x.Id == f.Initial) ?? f.Options?.FirstOrDefault();
                control = select;
                readers.Add(f.Key, () => J.N((select.SelectedItem as Option)?.Id ?? ""));

            } else if (f.Type == "multi") {

                var group = Stack();
                foreach (var option in f.Options ?? []) group.Children.Add(new CheckBox { Content = option.Name, Tag = option.Id, MinHeight = 44, IsChecked = f.Initial.Split(",").Contains(option.Id) });
                control = group;
                readers.Add(f.Key, () => J.N(group.Children.OfType<CheckBox>().Where(x => x.IsChecked == true).Select(x => x.Tag?.ToString() ?? "").ToArray()));

            } else {

                var input = Input(f.Label, f.Initial, f.Type == "password");
                if (f.Type == "multiline") { input.AcceptsReturn = true; input.MinHeight = 90; input.TextWrapping = TextWrapping.Wrap; }
                control = input;
                if (f.Key == "tendered" && f.Type == "money") { var presets = Wrap(); foreach (var amount in new[] { 50, 100, 200, 500, 1000 }) { var denomination = amount; presets.Children.Add(Button("$" + denomination, () => { input.Text = denomination.ToString(CultureInfo.InvariantCulture); return Task.CompletedTask; })); } control = Stack(input, Text("Total recibido: selecciona una denominación o escribe el importe.", 11, Muted), presets); }
                readers.Add(f.Key, () => {

                    var text = input.Text ?? "";
                    return f.Type switch {
                        "int" => J.N(long.Parse(text, CultureInfo.InvariantCulture)),
                        "decimal" => J.N(decimal.Parse(text.Replace(',', '.'), CultureInfo.InvariantCulture)),
                        "money" => MoneyInput(text),
                        "lines" => J.N(text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
                        _ => J.N(text)
                    };

                });

            }
            AutomationProperties.SetName(control, f.Label);
            rows.Children.Add(Stack(Text(f.Label, 12, Muted), control));

        }
        rows.Children.Add(Button("Guardar", async () => { var p = new JsonObject(); foreach (var reader in readers) p[reader.Key] = reader.Value(); var previous = modal; await accept(p); if (ReferenceEquals(modal, previous)) CloseModal(); }, true));
        ShowModal(title, rows);
        return Task.CompletedTask;

    }
    static JsonNode? MoneyInput(string text) { var amount = decimal.Parse(text.Replace(',', '.'), CultureInfo.InvariantCulture); Rules.Need(amount * 100 == decimal.Truncate(amount * 100), "Usa como máximo dos decimales"); return J.N(checked((long)(amount * 100))); }
    void ShowModal(string title, Control form, double width = 580) {

        CloseModal();
        restoreFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(title == "Agregar productos al borrador" ? Stack(Eyebrow("PERSONALIZA EL PEDIDO"), Text(title, 22, Graphite, true)) : Text(title, 22, Graphite, true));
        var close = Button("×", () => { CloseModal(); return Task.CompletedTask; });
        AutomationProperties.SetName(close, "Cerrar diálogo");
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        var panel = Stack(header, form);
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
        var card = Card(panel);
        card.MaxWidth = width;
        card.Width = Math.Max(0, Math.Min(width, ViewWidth - 40));
        card.Margin = new Thickness(20, 25);
        card.CornerRadius = new CornerRadius(17);
        card.Padding = new Thickness(ViewWidth <= 650 ? 21 : 28);
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        var scroller = new ScrollViewer { Content = card, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        modal = new Grid { Background = Brush.Parse("#73101C35") };
        modal.Children.Add(scroller);
        Grid.SetRowSpan(modal, 3);
        modal.ZIndex = 100;
        shell.Children.Add(modal);
        AutomationProperties.SetName(modal, title);
        PrototypeMotion.Enter(modal, 180, 0, 0);
        PrototypeMotion.Enter(card, 360, 0, 18);
        modal.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape && !busy) { CloseModal(); e.Handled = true; } }, RoutingStrategies.Tunnel);
        Dispatcher.UIThread.Post(() => { var focus = FindFocus(form); focus?.Focus(); });

    }
    static Control? FindFocus(Control c) { if (c is TextBox or ComboBox or Avalonia.Controls.Button or CheckBox) return c; if (c is Panel p) foreach (var child in p.Children) { var found = FindFocus(child); if (found is not null) return found; } if (c is Decorator d && d.Child is Control childControl) return FindFocus(childControl); return null; }
    void CloseModal() { if (modal is not null) { shell.Children.Remove(modal); modal = null; restoreFocus?.Focus(); } }
    Task Confirm(string title, Func<Task> action) { ShowModal(title, Stack(Text("Se validará y guardará en el servidor local."), Button("Confirmar", async () => { await action(); CloseModal(); }, true))); return Task.CompletedTask; }
}