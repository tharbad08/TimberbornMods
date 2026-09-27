using System;

namespace Bindito.Core
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class Context : Attribute
    {
        public string Name { get; }
        public Context(string name) { Name = name; }
    }

    public interface IConfigurator
    {
        void Configure(IContainerDefinition containerDefinition);
    }

    public interface IContainerDefinition
    {
        IBindingBuilder<T> Bind<T>();
    }

    public interface IBindingBuilder<T>
    {
        void AsSingleton();
    }
}