using System.Security.Cryptography;
using System.Text;

namespace Arr411.Core.Config;

/// <summary>Windows DPAPI, current user scope: the file is unreadable from another account or another PC.</summary>
public sealed class DpapiProtector : IDataProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Arr411.Desktop.config.v1");

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
