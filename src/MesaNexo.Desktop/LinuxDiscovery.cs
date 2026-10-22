using System.Diagnostics;
using System.Text.Json.Nodes;
using MesaNexo.Client;
using MesaNexo.Core;
namespace MesaNexo.Desktop;
public sealed class LinuxDiscovery:IDiscoveryProvider
{
    public async Task<IReadOnlyList<DiscoveryCandidate>> Browse(CancellationToken ct=default)
    {
        var start=new ProcessStartInfo("/usr/local/sbin/culinaryos-discover"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        using var process=Process.Start(start)??throw new IOException("Descubrimiento CulinaryOS no disponible");var output=await process.StandardOutput.ReadToEndAsync(ct);await process.WaitForExitAsync(ct);if(process.ExitCode!=0)throw new IOException("No se pudo consultar Avahi; usa la dirección manual");
        return JsonNode.Parse(output)!.AsArray().Objects().Select(x=>new DiscoveryCandidate(x.S("address"),x.I("port"),x.S("name"))).ToArray();
    }
}