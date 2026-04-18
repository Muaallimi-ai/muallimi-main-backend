using System;

namespace Muallimi.Api.Leaderboards.LeaderboardComputation;

/// <summary>
/// T145 (US7) — Privacy projection applied at snapshot time.
///
/// <see cref="PrivacyModes.RealName"/> preserves the display name.
/// <see cref="PrivacyModes.FirstNameOnly"/> truncates to the first token.
/// <see cref="PrivacyModes.Pseudonym"/> replaces with a deterministic
/// pseudonym seeded from the student id so the same student shows the same
/// label across snapshots. Admin views always receive the real display
/// name and bypass this projector.
/// </summary>
public static class PrivacyModes
{
    public const string RealName = "real_name";
    public const string FirstNameOnly = "first_name_only";
    public const string Pseudonym = "pseudonym";

    public static bool IsValid(string mode) =>
        mode == RealName || mode == FirstNameOnly || mode == Pseudonym;
}

public static class LeaderboardPrivacyProjector
{
    private static readonly string[] PseudonymPool =
    {
        "Eagle", "Falcon", "Lion", "Tiger", "Dolphin", "Hawk", "Panther", "Cheetah",
        "Wolf", "Fox", "Bear", "Shark", "Orca", "Jaguar", "Lynx", "Raven",
    };

    public static string Apply(string mode, string realName, Guid studentId)
    {
        if (string.IsNullOrWhiteSpace(realName)) realName = "Student";
        return mode switch
        {
            PrivacyModes.RealName => realName,
            PrivacyModes.FirstNameOnly => FirstToken(realName),
            PrivacyModes.Pseudonym => Pseudonymise(studentId),
            _ => FirstToken(realName),
        };
    }

    private static string FirstToken(string name)
    {
        var trimmed = name.Trim();
        var space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed.Substring(0, space);
    }

    private static string Pseudonymise(Guid studentId)
    {
        var bytes = studentId.ToByteArray();
        var index = Math.Abs(BitConverter.ToInt32(bytes, 0)) % PseudonymPool.Length;
        var suffix = (Math.Abs(BitConverter.ToInt32(bytes, 4)) % 900) + 100;
        return $"{PseudonymPool[index]}-{suffix}";
    }
}
