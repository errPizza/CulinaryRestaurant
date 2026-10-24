using System.Text.Json.Nodes;
namespace MesaNexo.Core;

public sealed record PacResult(string Status, string ProviderId = "", string FiscalUuid = "", string Message = "");
public interface IPacAdapter {
    bool IsSandbox { get; }
    Task<PacResult> Stamp(JsonObject frozen, CancellationToken ct = default);
    Task<PacResult> Consult(string id, CancellationToken ct = default);
    Task<PacResult> Cancel(string id, string fiscalUuid, string motive, string? replacement = null, CancellationToken ct = default);
}
