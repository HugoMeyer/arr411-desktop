using System.Security.Cryptography;
using System.Text;
using Arr411.Core.Config;

namespace Arr411.Core.Tests;

/// <summary>The real Windows data protection (the other tests use a stand-in): what ends up on disk really is unreadable.</summary>
public class DpapiProtectorTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    private static readonly byte[] Secret = Encoding.UTF8.GetBytes($$"""{"radarrKey":"{{Key}}"}""");

    private static bool Contains(byte[] data, string text) =>
        data.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0 || data.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;

    [Fact]
    public void Protected_data_comes_back_unchanged()
    {
        var protector = new DpapiProtector();

        Assert.Equal(Secret, protector.Unprotect(protector.Protect(Secret)));
    }

    [Fact]
    public void Protected_data_holds_nothing_readable_of_what_it_protects()
    {
        var blob = new DpapiProtector().Protect(Secret);

        Assert.False(Contains(blob, Key));
        Assert.False(Contains(blob, "radarrKey"));
    }

    [Fact]
    public void The_same_data_is_not_protected_the_same_way_twice()
    {
        var protector = new DpapiProtector();

        Assert.NotEqual(protector.Protect(Secret), protector.Protect(Secret));
    }

    [Fact]
    public void A_program_that_does_not_know_the_entropy_of_the_app_cannot_read_it()
    {
        var blob = new DpapiProtector().Protect(Secret);

        Assert.ThrowsAny<CryptographicException>(() => ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
    }

    [Fact]
    public void Data_that_another_program_protected_is_refused()
    {
        var foreign = ProtectedData.Protect(Secret, null, DataProtectionScope.CurrentUser);

        Assert.ThrowsAny<CryptographicException>(() => new DpapiProtector().Unprotect(foreign));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(200)]
    public void Bytes_that_are_not_protected_data_are_refused(int length)
    {
        var junk = RandomNumberGenerator.GetBytes(length);

        Assert.ThrowsAny<CryptographicException>(() => new DpapiProtector().Unprotect(junk));
    }

    [Fact]
    public void The_settings_file_and_its_backup_hold_no_key_in_clear_and_open_again()
    {
        using var dir = new TempDir();
        var path = dir.File("config.dat");
        new ConfigStore(path, new DpapiProtector()).Merge($$"""{"radarrUrl":"http://nas:7878","radarrKey":"{{Key}}"}""", "test");
        new ConfigStore(path, new DpapiProtector()).Merge("""{"sonarrUrl":"http://nas:8989"}""", "test");

        var files = Directory.GetFiles(dir.Root);
        Assert.Contains(path + ".bak", files);
        foreach (var file in files)
        {
            var raw = File.ReadAllBytes(file);
            Assert.False(Contains(raw, Key), file);
            Assert.False(Contains(raw, "radarrKey"), file);
        }

        var reopened = new ConfigStore(path, new DpapiProtector());
        Assert.Equal(Key, reopened.GetString("radarrKey"));
        Assert.Equal("http://nas:8989", reopened.GetString("sonarrUrl"));
    }

    [Fact]
    public void A_settings_file_in_clear_is_not_used_and_is_set_aside()
    {
        using var dir = new TempDir();
        var path = dir.File("config.dat");
        var plain = $$"""{"radarrKey":"{{Key}}"}""";
        File.WriteAllText(path, plain);

        var store = new ConfigStore(path, new DpapiProtector());

        Assert.Equal("{}", store.ToJson());
        Assert.Equal(plain, File.ReadAllText(path + ".bad"));
    }
}
