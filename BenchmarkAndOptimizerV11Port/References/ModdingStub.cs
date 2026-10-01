namespace Timberborn.Modding;

public interface IModStarter
{
    void StartMod(IModEnvironment modEnvironment);
}

public interface IModEnvironment
{
    string ModPath { get; }
}
