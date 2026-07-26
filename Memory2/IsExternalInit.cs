// Compatibility shim for C# 9+ init-only members and record types when targeting netstandard2.0.
// Newer target frameworks provide this type in System.Runtime.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
