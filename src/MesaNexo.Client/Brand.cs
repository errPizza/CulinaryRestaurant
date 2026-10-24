using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
namespace MesaNexo.Client;

public static class Brand {
    public static FontFamily InterfaceFont { get; } = OperatingSystem.IsWindows() ? new FontFamily("Segoe UI") : new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter");
    public const string Name = "Culinary Restaurant";
    static readonly Dictionary<string, string> Paths = new() { { "arrow-up-right", "M7 17 17 7M7 7h10v10" }, { "arrow-right", "M4 12h16m-6-6 6 6-6 6" }, { "chevron-down", "m6 9 6 6 6-6" }, { "drink", "M6 7h12l-1.5 14h-9zM5 7h14M14 7l2-5h4" }, { "dessert", "M4 11h16v9H4zM4 15h16M7 11c-4-6 6-7 5-1M12 10c0-6 8-5 6 1" }, { "grid", "M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z" }, { "food", "M5 3v8m4-8v8M3 7h8M7 11v10M18 3c-5 4-5 9 0 9v9M18 3v9" }, { "bell", "M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4" }, { "chart", "M4 20V10h4v10M10 20V4h4v16M16 20v-8h4v8" }, { "users", "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8M18 8a3 3 0 0 1 0 6M22 21v-2a4 4 0 0 0-3-4" }, { "calendar", "M4 5h16v16H4zM8 2v6M16 2v6M4 11h16" }, { "settings", "M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8M12 2v3M12 19v3M2 12h3M19 12h3M5 5l2 2M17 17l2 2M5 19l2-2M17 7l2-2" }, { "wallet", "M3 5h17v15H3zM15 10h6v5h-6z" }, { "cloud", "M6 18a5 5 0 1 1 1-10 6 6 0 0 1 11 1 4 4 0 0 1 0 9H6M12 8v8m-3-3 3 3 3-3" }, { "exit", "M9 3H3v18h6M12 12h9m-4-4 4 4-4 4" }, { "check", "m5 12 4 4 10-10" }, { "book", "M3 4h7l2 2 2-2h7v16h-7l-2 2-2-2H3zM12 6v16" }, { "help", "M9 9a3 3 0 0 1 6 0c0 2-3 2-3 5M12 18v1M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20" } };
    public static Control Icon(string name, IBrush? color = null, double size = 20) { var canvas = new Canvas { Width = 24, Height = 24 }; canvas.Children.Add(new Avalonia.Controls.Shapes.Path { Data = StreamGeometry.Parse(Paths.GetValueOrDefault(name, Paths["grid"])), Stroke = color ?? Brush.Parse("#536173"), StrokeThickness = 1.6, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round }); return new Viewbox { Width = size, Height = size, Child = canvas }; }
    static Avalonia.Media.Imaging.Bitmap Load(string name) { using var stream = AssetLoader.Open(new Uri("avares://MesaNexo.Client/Assets/" + name)); return new Avalonia.Media.Imaging.Bitmap(stream); }
    static readonly Lazy<Avalonia.Media.Imaging.Bitmap> Symbol = new(() => Load("symbol.png")), Wordmark = new(() => Load("wordmark.png"));
    public static Image Image(bool letters = false, double width = 40, double height = 40) => new() { Source = letters ? Wordmark.Value : Symbol.Value, Width = width, Height = height, Stretch = Stretch.Uniform };
}
