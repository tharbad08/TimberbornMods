namespace Timberborn.ModManagerScene
{
    public interface IModEnvironment
    {
        string ModPath { get; }
    }

    public interface IModStarter
    {
        void StartMod(IModEnvironment modEnvironment);
    }
}
