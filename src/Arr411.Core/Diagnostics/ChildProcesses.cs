using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Arr411.Core.Diagnostics;

/// <summary>
/// The processes that the program started, and those they started in turn: the browser behind the web views, with its renderers. What they hold is not
/// in the memory of the program itself, and a web page that grew too big shows there first.
/// </summary>
public sealed record ChildProcesses(int Count, long PrivateBytes, IReadOnlyList<long> Largest)
{
    private const uint SnapProcess = 2;
    private static readonly IntPtr InvalidHandle = new(-1);

    public static ChildProcesses Take() => Take(Environment.ProcessId);

    internal static ChildProcesses Take(int root)
    {
        var sizes = new List<long>();
        foreach (var id in Descendants(root))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                sizes.Add(process.PrivateMemorySize64);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // It ended since the list was made.
            }
        }

        sizes.Sort((a, b) => b.CompareTo(a));
        return new ChildProcesses(sizes.Count, sizes.Sum(), sizes.Take(3).ToList());
    }

    /// <summary>The ids of the processes that descend from <paramref name="root"/> (its children, theirs...), not the root itself.</summary>
    internal static List<int> Descendants(int root)
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle) return [];

        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
            {
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var children = parents.ToLookup(pair => pair.Value, pair => pair.Key);
        var found = new List<int>();
        var seen = new HashSet<int> { root };
        var queue = new Queue<int>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            foreach (var child in children[queue.Dequeue()])
            {
                if (!seen.Add(child)) continue;
                found.Add(child);
                queue.Enqueue(child);
            }
        }

        return found;
    }

    public override string ToString() => Count == 0
        ? "no child process"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{Count} child processes hold {Humanize.Megabytes(PrivateBytes)} MB (private) in all, the biggest: {string.Join(", ", Largest.Select(Humanize.Megabytes))} MB");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
