using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MesaNexo.Client;
using MesaNexo.Core;
namespace MesaNexo.Desktop;
// User data only. No branch data key or TLS private key reaches this process.
public sealed class DesktopVault(string? testDirectory = null): ISecureVault {
    readonly string directory = testDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MesaNexo", "vault");
    string PathFor(string key) => Path.Combine(directory, J.Hash(key) + ".dat");
    public async Task<string?> Read(string key) {

        if (OperatingSystem.IsWindows()) { var p = PathFor(key); return File.Exists(p) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(await File.ReadAllBytesAsync(p), Encoding.UTF8.GetBytes(key), DataProtectionScope.CurrentUser)) : null; }
        return await SecretTool(["lookup", "application", "MesaNexo", "key", key], null, true);

    }
    public async Task Write(string key, string value) {

        if (OperatingSystem.IsWindows()) {

            Directory.CreateDirectory(directory);
            var path = PathFor(key);
            var tmp = path + "." + J.Id() + ".tmp";
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(key), DataProtectionScope.CurrentUser);
            await using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { await stream.WriteAsync(bytes); stream.Flush(true); }
            File.Move(tmp, path, true);
            return;

        }
        await SecretTool(["store", "--label=Culinary Restaurant", "application", "MesaNexo", "key", key], value, false);

    }
    public async Task Delete(string key) { if (OperatingSystem.IsWindows()) { File.Delete(PathFor(key)); return; } await SecretTool(["clear", "application", "MesaNexo", "key", key], null, true); }
    static async Task<string?> SecretTool(string[] args, string? input, bool missingAllowed) {

        var start = new ProcessStartInfo("secret-tool") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var p = Process.Start(start) ?? throw new IOException("Activa el almacén de credenciales de Linux");
        if (input is not null) await p.StandardInput.WriteAsync(input);
        p.StandardInput.Close();
        var error = p.StandardError.ReadToEndAsync();
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        // Exit 1 alone also covers a keyring that failed: reading that as "missing" would mint a new device identity.
        if (p.ExitCode != 0) { if (missingAllowed && p.ExitCode == 1 && (await error).Trim() == "") return null; throw new IOException("El almacén seguro no está disponible; desbloquea el llavero de sesión"); }
        return output.TrimEnd('\r', '\n');

    }
}