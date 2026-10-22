using System.Net;
using Foundation;
using MesaNexo.Client;
namespace MesaNexo.iOS;
// Uses Apple's Bonjour API. Candidates still require provisioned CA, TLS name and site verification.
public sealed class IosDiscovery:IDiscoveryProvider
{
    public async Task<IReadOnlyList<DiscoveryCandidate>> Browse(CancellationToken ct=default)
    {
        using var browser=new NSNetServiceBrowser();var services=new List<NSNetService>();var result=new List<DiscoveryCandidate>();var stopping=false;
        browser.FoundService+=(_,e)=>{
            if(stopping)return;var service=e.Service;services.Add(service);service.AddressResolved+=(_,_)=>{
                if(stopping)return;foreach(var address in service.Addresses){var raw=address.ToArray();if(raw.Length>=8&&raw[1]==2){var ip=new IPAddress(raw.AsSpan(4,4));var port=checked((int)service.Port);if(port is >=1024 and <=65535)result.Add(new(ip.ToString(),port,service.Name));}}
            };service.Resolve(4);
        };
        browser.SearchForServices("_culinaryos._tcp.","local.");try{await Task.Delay(5000,ct);}finally{stopping=true;browser.Stop();foreach(var service in services){service.Stop();service.Dispose();}}
        return result.Distinct().ToArray();
    }
}