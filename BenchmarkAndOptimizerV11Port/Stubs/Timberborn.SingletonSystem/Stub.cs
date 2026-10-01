namespace Timberborn.SingletonSystem
{
    public interface ILoadableSingleton
    {
        void Load();
    }

    public interface IUpdatableSingleton
    {
        void UpdateSingleton();
    }
}
