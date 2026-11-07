using System.Text.Json.Nodes;
namespace MesaNexo.Core;
// What the Linux update journal (/var/lib/culinary-update/state.json, written by apply.py) means for the screens: an update
// on its way, how it ended, or nothing worth showing. Old entries are ignored, so a finished update does not come back.
public static class UpdateProgress {
    public const string StateFile = "/var/lib/culinary-update/state.json";
    static readonly string[] Active = ["received", "verified", "backup", "snapshot", "installing", "installed"];
    static readonly string[] Ended = ["healthy", "rolled-back", "failed"];
    public static JsonObject? From(JsonObject? journal, DateTimeOffset now) {

        if (journal is null) return null;
        var stage = journal.S("stage");
        var history = journal.A("history").Objects().ToArray();
        if (history.Length == 0 || !Active.Contains(stage) && !Ended.Contains(stage)) return null;
        var at = DateTimeOffset.FromUnixTimeMilliseconds((long)(history[^1]["at"]?.GetValue<double>() * 1000 ?? 0));
        var since = DateTimeOffset.FromUnixTimeMilliseconds((long)(history.LastOrDefault(h => h.S("stage") == "received")?["at"]?.GetValue<double>() * 1000 ?? 0));
        // An update in progress for longer than this stalled somewhere; its end is shown for a short while only.
        if (now - at > (Active.Contains(stage) ? TimeSpan.FromMinutes(45) : TimeSpan.FromMinutes(30))) return null;
        return J.O("stage", stage, "active", Active.Contains(stage), "version", journal.S("offer"), "reason", journal.S("reason"), "at", at.ToString("O"), "since", since.ToString("O"));

    }
    public static JsonObject? Read(string path = StateFile) {

        try { return File.Exists(path) ? From(JsonNode.Parse(File.ReadAllText(path)) as JsonObject, DateTimeOffset.UtcNow) : null; } catch (Exception) { return null; }

    }
}
