namespace MesaNexo.Client;

public record DiscoveryCandidate(string Address, int Port, string DisplayName);
public interface IDiscoveryProvider { Task<IReadOnlyList<DiscoveryCandidate>> Browse(CancellationToken ct = default); }