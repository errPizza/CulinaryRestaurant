namespace MesaNexo.Core;

// What this build speaks. docs/COMPATIBILIDAD.md says which versions work together and when each number must change.
public static class Contract {
    // HTTP API between clients and the branch. Raised only by a change an older client cannot survive.
    public const int Api = 1;
    // Shape of the branch's records. A backend never runs on a database newer than itself.
    public const int Schema = 2;
    public const string Events = "culinaryos-events-v1";
}
