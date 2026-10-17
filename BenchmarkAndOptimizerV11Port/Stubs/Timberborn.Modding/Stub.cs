namespace Timberborn.Modding
{
    public class ModRepository
    {
        public System.Collections.Generic.IEnumerable<Mod> EnabledMods { get; } = System.Array.Empty<Mod>();
    }

    public class Mod
    {
        public ModManifest Manifest { get; } = new ModManifest();
        public ModDirectory ModDirectory { get; } = new ModDirectory();
    }

    public class ModManifest
    {
        public string Id { get; } = "";
        public string Name { get; } = "";
    }

    // The actual Timberborn.Modding.ModDirectory is a value type, not a class.
    // This matters to Mod.get_ModDirectory's CLR method signature.
    public struct ModDirectory
    {
        public string OriginPath => "";
    }
}
