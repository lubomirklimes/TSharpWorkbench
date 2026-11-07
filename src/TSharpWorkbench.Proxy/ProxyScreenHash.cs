using TSharpWorkbench.Testing;

namespace TSharpWorkbench.Proxy;

/// <summary>Deterministic, allocation-free 64-bit FNV-1a hash of a logical screen (geometry, characters, attributes).
/// Identical cells give an identical hash on every run and platform; it is a change signal, not a security digest.</summary>
internal static class ProxyScreenHash
{
    private const ulong Offset = 14695981039346656037UL, Prime = 1099511628211UL;

    internal static ulong Compute(ScreenSnapshot screen)
    {
        ulong hash = Offset;
        hash = Mix(hash, (uint)screen.Width); hash = Mix(hash, (uint)screen.Height);
        for (int y = 0; y < screen.Height; y++)
            for (int x = 0; x < screen.Width; x++)
            {
                var cell = screen[x, y];
                hash = Mix(hash, cell.Character | ((uint)cell.Attr << 16));
            }
        return hash;
    }

    private static ulong Mix(ulong hash, uint value)
    {
        for (int i = 0; i < 4; i++, value >>= 8) hash = (hash ^ (value & 0xff)) * Prime;
        return hash;
    }
}
