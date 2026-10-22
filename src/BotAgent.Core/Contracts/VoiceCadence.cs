namespace BotAgent.Services.Voice;

/// <summary>Single source for the suggested interval between voice messages.</summary>
public static class VoiceCadence
{
    public static int IntervalSeconds(int eagerness) => Math.Clamp(eagerness, 0, 100) switch
    {
        <= 20 => 180,
        <= 40 => 90,
        <= 60 => 45,
        <= 80 => 25,
        _ => 15
    };
}
