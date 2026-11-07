using System.Reflection;
using System.Runtime.Loader;

namespace TSharpWorkbench.Testing;

internal static class NativeProviderDeployment
{
    private static readonly Dictionary<string, Assembly> Loaded = new(StringComparer.Ordinal);
    internal static void ValidateIdentity(string path, string provider)
    {
        if (AssemblyName.GetAssemblyName(path).Name != provider)
            throw new InvalidOperationException("Native provider assembly identity does not match its canonical filename. Rebuild and deploy matching outputs.");
    }

    internal static Assembly Load(string provider)
    {
        lock (Loaded)
        {
            if (Loaded.TryGetValue(provider, out var loaded)) return loaded;
            // Called only with the two fixed internal provider identities, never a wire-supplied name/path.
            if (provider is not ("TSharpWorkbench.Diagnostics.NativePixels" or "TSharpWorkbench.Diagnostics.NativeHosts"))
                throw new InvalidOperationException("Unknown internal native provider.");
            string path = Path.Combine(Path.GetDirectoryName(typeof(NativeProviderDeployment).Assembly.Location)!, provider + ".dll");
            if (!File.Exists(path))
                throw new FileNotFoundException("Native provider deployment is incomplete. Stage the provider build output beside Testing, including its helper apphost, deps/runtimeconfig and native assets.", path);
            ValidateIdentity(path, provider);
            string legacy = provider.Replace("TSharpWorkbench.Diagnostics.", "TSharpVision.Diagnostics.", StringComparison.Ordinal);
            if (AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().Name == legacy))
                throw new InvalidOperationException("Stale legacy native provider is already loaded; restart with a canonical deployment.");
            var resolver = new AssemblyDependencyResolver(path);
            Assembly? Resolve(AssemblyLoadContext context, AssemblyName name) =>
                resolver.ResolveAssemblyToPath(name) is { } dependency ? context.LoadFromAssemblyPath(dependency) : null;
            IntPtr ResolveNative(Assembly assembly, string name) =>
                resolver.ResolveUnmanagedDllToPath(name) is { } dependency ? System.Runtime.InteropServices.NativeLibrary.Load(dependency) : IntPtr.Zero;
            AssemblyLoadContext.Default.Resolving += Resolve;
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveNative;
            try
            {
                loaded = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                Loaded.Add(provider, loaded);
                return loaded;
            }
            catch
            {
                AssemblyLoadContext.Default.Resolving -= Resolve;
                AssemblyLoadContext.Default.ResolvingUnmanagedDll -= ResolveNative;
                throw;
            }
        }
    }
}
