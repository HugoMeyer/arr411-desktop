using System.Diagnostics;
using Arr411.Core.Config;

namespace Arr411.Core.Update;

/// <summary>What <see cref="UpdateService"/> needs from its surroundings; the tests give it fakes for every one of them.</summary>
public sealed class UpdateOptions
{
    /// <summary>The environment variable that switches the updates off for a run (<c>off</c>): tests and portable runs. It can only disable.</summary>
    public const string DisableVariable = "ARR411_UPDATE";

    public required AppPaths Paths { get; init; }

    /// <summary>The settings: the service reads <c>autoUpdate</c> and follows its changes.</summary>
    public required ConfigStore Config { get; init; }

    /// <summary>The version that runs, "0.2.1".</summary>
    public required string CurrentVersion { get; init; }

    /// <summary>The file of the program that runs (<c>Environment.ProcessPath</c>): what an update replaces.</summary>
    public required string ExePath { get; init; }

    /// <summary>What the applier waits for to end before it swaps the files.</summary>
    public int ProcessId { get; init; } = Environment.ProcessId;

    /// <summary>The network; null for the real one. Tests give a fake that answers from memory.</summary>
    public HttpMessageHandler? Handler { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Starts the applier. False when the program could not be started.</summary>
    public Func<ProcessStartInfo, bool> StartProcess { get; init; } = StartDetached;

    /// <summary>Whether the folder of the program can be written to: the swap needs it. The real answer comes from trying.</summary>
    public Func<string, bool> CanWriteFolder { get; init; } = UpdateProbe.CanWriteTo;

    /// <summary>No check, no download, no timer: the run is not to touch the network (see <see cref="DisableVariable"/>).</summary>
    public bool Disabled { get; init; }

    /// <summary>Whether a downloaded update may be applied without being asked for (at start, at exit). False for a debug build.</summary>
    public bool AutoApply { get; init; } = true;

    public TimeSpan FirstCheckDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>The least time between two checks that the user asks for.</summary>
    public TimeSpan ManualCooldown { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How long GitHub has to answer a question of the API.</summary>
    public TimeSpan ApiTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a download may go without receiving a byte.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The least time between two progress reports.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a leftover of the last swap that is still in use is left alone, before it is deleted a second time.</summary>
    public TimeSpan CleanUpRetry { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>True when <see cref="DisableVariable"/> is set to <c>off</c>.</summary>
    public static bool IsOff(string? variableValue) => string.Equals(variableValue?.Trim(), "off", StringComparison.OrdinalIgnoreCase);

    public static bool DisabledByEnvironment() => IsOff(Environment.GetEnvironmentVariable(DisableVariable));

    // The applier must outlive this process: it is its own program, with no window and no handles in common.
    internal static bool StartDetached(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
        return process is not null;
    }
}
