// Source follows GameAssemblyPublicizer/IgnoresAccessChecks.cs used by ModdableWeathers.
// Compile against publicized game members and preserve the runtime field's original name.
[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute("Timberborn.OptionsGame")]

namespace System.Runtime.CompilerServices
{
    [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : System.Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName)
        {
        }
    }
}
