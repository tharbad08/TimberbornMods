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

    public class ModDirectory
    {
        public string OriginPath { get; } = "";
    }
}
