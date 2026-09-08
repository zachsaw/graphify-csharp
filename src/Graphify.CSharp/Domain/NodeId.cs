using System.Security.Cryptography;
using System.Text;

namespace Graphify.CSharp.Domain;

public static class NodeId
{
    public static string ForSymbol(SymbolIdentity symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return ForKey(symbol.CanonicalKey);
    }

    public static string ForKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return "cs_" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
