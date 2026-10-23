using System.Text.Json.Nodes;
using static MesaNexo.Core.Rules;
namespace MesaNexo.Core;
// Live integrations: what the domain queues for the server's services and what it does with their answers. The rules are the
// simulators' (Integrations.cs); only who answers changes.
public sealed partial class Restaurant
{
    // Effects of a provider's answer, applied in the same commit that stores it (IntegrationResult).
    void LiveEffects(BranchState s,JsonObject u,string kind,JsonObject row)
    {
        switch(kind){
        }
    }
}
