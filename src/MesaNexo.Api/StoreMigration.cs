using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MesaNexo.Core;
namespace MesaNexo.Api;

// One-time move of a branch from the CulinaryOS SQLite store into PostgreSQL. It reads what runtime/export_store.py
// writes and commits it all in one transaction, or nothing: versions, receipts, the outbox with its acknowledgements
// and the outbox state. Only an empty PostgreSQL store is filled; the SQLite file stays as the fallback.
public static class StoreMigration {
    public static async Task<JsonObject> Import(PostgresBranchStore store, TextReader export, CancellationToken ct = default) {

        await using var db = await store.Data.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await PostgresBranchStore.Exec(db, tx, "SELECT pg_advisory_xact_lock(@l)", ct, ("l", PostgresBranchStore.WriterLock));
        Rules.Need(await PostgresBranchStore.Scalar<long>(db, tx, "SELECT (SELECT count(*) FROM records) + (SELECT count(*) FROM events)", ct) == 0, "La base de PostgreSQL ya tiene datos; la migración sólo llena una vacía");
        long records = 0, events = 0;
        JsonObject? end = null;
        // A shell pipe may prefix a byte order mark or add blank lines; neither is part of the export.
        async Task<string?> Next() { while (await export.ReadLineAsync(ct) is { } raw) { var line = raw.TrimStart((char)0xFEFF).Trim(); if (line.Length > 0) return line; } return null; }
        var header = JsonNode.Parse(await Next() ?? "null") as JsonObject;
        Rules.Need(header?.S("type") == "header" && header.S("site_id") == store.Site && header.L("schema") == 1, "La exportación no es de esta sucursal");
        while (await Next() is { } line) {

            var item = (JsonObject)JsonNode.Parse(line)!;
            switch (item.S("type")) {
                case "metadata":
                    // The old outbox state carries over; a store that once dropped events still owes a central its baseline.
                    var key = item.S("key");
                    var value = item.S("value");
                    if (key == "mesanexo_outbox" && JsonNode.Parse(value)!.S("mode") == "standalone") value = J.Canonical(J.O("mode", "standalone-dropped"));
                    await PostgresBranchStore.Exec(db, tx, "INSERT INTO store_metadata VALUES(@k, @v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ct, ("k", key), ("v", value));
                    break;
                case "record":
                    await store.ImportRecord(db, tx, item.S("id"), item.L("version"), item["data"], ct);
                    records++;
                    break;
                case "event":
                    var raw = Convert.FromBase64String(item.S("raw"));
                    Rules.Need(Convert.ToHexStringLower(SHA256.HashData(raw)) == item.S("digest"), "Integridad de evento inválida en la exportación");
                    await store.ImportEvent(db, tx, item, raw, ct);
                    events++;
                    break;
                case "end":
                    end = item;
                    break;
                default:
                    throw new BusinessException(400, "Línea de exportación desconocida");
            }

        }
        Rules.Need(end is not null && end.L("records") == records && end.L("events") == events, "La exportación llegó incompleta; no se migró nada");
        // A central later receives exactly what the old store still owed it.
        if (await PostgresBranchStore.Scalar<string>(db, tx, "SELECT value FROM store_metadata WHERE key='mesanexo_outbox'", ct) is null && await PostgresBranchStore.Scalar<string>(db, tx, "SELECT value FROM store_metadata WHERE key='mesanexo_outbox_dropped'", ct) is not null) await PostgresBranchStore.Exec(db, tx, "INSERT INTO store_metadata VALUES('mesanexo_outbox', @v)", ct, ("v", J.Canonical(J.O("mode", "standalone-dropped"))));
        await tx.CommitAsync(ct);
        return J.O("migrated", true, "site_id", store.Site, "records", records, "events", events);

    }
}
