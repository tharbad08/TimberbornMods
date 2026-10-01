using System;
namespace Bindito.Core
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class ContextAttribute : Attribute { public ContextAttribute(string context) { } }
    public abstract class Configurator { public virtual void Configure() { } }
}
namespace Bindito.Core.Internal
{
    public enum Scope { Singleton, Transient }
}
