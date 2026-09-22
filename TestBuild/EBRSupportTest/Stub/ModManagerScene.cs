namespace Timberborn.ModManagerScene;

public interface IModEnvironment
{
}

public interface IModStarter
{
    void StartMod(IModEnvironment modEnvironment);
}
