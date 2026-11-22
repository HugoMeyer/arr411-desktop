namespace Arr411.Core.Platform;

/// <summary>What became of a start that looked for the instance it would replace (see <see cref="SingleInstance.AdmitAsync"/>).</summary>
public enum Admission
{
    /// <summary>No other instance runs: this start is the instance.</summary>
    Instance,

    /// <summary>Another instance runs and took what this start was for: this start has nothing left to do.</summary>
    HandedOver,

    /// <summary>Another instance runs and does not answer.</summary>
    Unreachable,
}
