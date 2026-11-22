namespace Arr411.Core.Bridge;

/// <summary>Windows dates the last key or mouse input with a tick count of 32 bits, which starts over every 49.7 days.</summary>
public static class UserInput
{
    /// <summary>
    /// How long ago the input happened. The tick counts are subtracted as unsigned numbers, which stays right when the count started over in
    /// between; an input that seems to come from the future (the clock was read before it) is not a recent one, so the answer is "very old".
    /// </summary>
    public static TimeSpan Age(uint lastInputTick, uint nowTick) => TimeSpan.FromMilliseconds(unchecked(nowTick - lastInputTick));
}
