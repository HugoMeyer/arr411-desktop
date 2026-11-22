namespace Arr411.Core.Update;

/// <summary>Finds out whether the program can be replaced where it lives, by trying to create a file there.</summary>
internal static class UpdateProbe
{
    public const string Prefix = "Arr411-probe-";

    public static bool CanWriteTo(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return false;

        try
        {
            var probe = Path.Combine(folder, $"{Prefix}{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
