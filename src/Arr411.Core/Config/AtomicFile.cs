namespace Arr411.Core.Config;

/// <summary>Writes a file so that a crash or a power cut never leaves it half written.</summary>
internal static class AtomicFile
{
    public static void Write(string path, byte[] data, bool keepBackup)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, data);

        if (!File.Exists(path)) File.Move(temp, path);
        else if (keepBackup) File.Replace(temp, path, path + ".bak");
        else File.Move(temp, path, overwrite: true);
    }
}
