using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MesaNexo.Core;
namespace MesaNexo.Client;
// Optional artwork is cached locally. It never controls acceptance of a business operation.
public sealed class ProductPicture: Image {
    bool detached;
    public ProductPicture(string source) {

        Width = 160;
        Height = 110;
        Stretch = Stretch.UniformToFill;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        DetachedFromVisualTree += (_, _) => detached = true;
        AttachedToVisualTree += async (_, _) => {

            detached = false;
            try {

                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MesaNexo", "artwork");
                var path = Path.Combine(directory, J.Hash(source) + ".image");
                byte[] bytes;
                if (File.Exists(path)) { if (new FileInfo(path).Length > 1024 * 1024) return; bytes = await File.ReadAllBytesAsync(path); } else {

                    if (source.StartsWith("data:image/")) { var comma = source.IndexOf(','); if (comma < 0) return; bytes = Convert.FromBase64String(source[(comma + 1)..]); } else { var uri = new Uri(source); if (uri.Scheme != "https" || uri.UserInfo != "") return; using var handler = new SocketsHttpHandler { AllowAutoRedirect = false }; using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) }; using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead); if (!response.IsSuccessStatusCode) return; await using var input = await response.Content.ReadAsStreamAsync(); using var output = new MemoryStream(); var buffer = new byte[8192]; int n; while ((n = await input.ReadAsync(buffer)) > 0) { if (output.Length + n > 1024 * 1024) return; await output.WriteAsync(buffer.AsMemory(0, n)); } bytes = output.ToArray(); }
                    if (bytes.Length > 1024 * 1024) return;
                    Directory.CreateDirectory(directory);
                    await File.WriteAllBytesAsync(path, bytes);

                }
                var bitmap = await Task.Run(() => Bitmap.DecodeToWidth(new MemoryStream(bytes), 240));
                await Dispatcher.UIThread.InvokeAsync(() => { if (!detached) Source = bitmap; else bitmap.Dispose(); });

            } catch { Dispatcher.UIThread.Post(() => { if (!detached) IsVisible = false; }); }

        };

    }
}