using System;

namespace Bindito.Core
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class ContextAttribute : Attribute
    {
        public string ContextName { get; }
        public ContextAttribute(string contextName) { ContextName = contextName; }
    }

    public interface IConfigurator
    {
        void Configure(IContainerDefinition containerDefinition);
    }

    public interface IContainerDefinition
    {
        ISingleBindingBuilder<T> Bind<T>();
    }

    public interface IBindingBuilder<T> { }

    public interface ISingleBindingBuilder<T> : IBindingBuilder<T>, IScopeAssignee { }

    public interface IScopeAssignee
    {
        IExportAssignee AsSingleton();
        IExportAssignee AsTransient();
    }

    public interface IExportAssignee
    {
        void AsExported();
    }
}