namespace Arr411.Core.Config;

/// <summary>Encrypts what is written to disk (API keys, passwords).</summary>
public interface IDataProtector
{
    byte[] Protect(byte[] data);

    /// <exception cref="System.Security.Cryptography.CryptographicException">The data was not protected by this user, or is corrupt.</exception>
    byte[] Unprotect(byte[] data);
}
