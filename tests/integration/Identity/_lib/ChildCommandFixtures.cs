using System;
using Muallimi.Application.Identity.Commands;

namespace Muallimi.Api.Tests.Identity;

/// <summary>
/// Shared fixture for the post-redesign <see cref="CreateChildCommand"/>
/// shape (year+month + curriculum + avatar + prefs + login method +
/// parental consent). Tests pass only the fields they care about and
/// inherit safe defaults for the rest.
/// </summary>
internal static class ChildCommandFixtures
{
    public static CreateChildCommand MakeCreateChild(
        Guid parentUserId,
        Guid parentTenantId,
        string fullName = "الطفل",
        int grade = 3,
        string? gender = "male",
        int birthYear = 2017,
        int birthMonth = 6,
        string curriculumType = "Moe",
        string? schoolName = null,
        string avatarEmoji = "🐸",
        string avatarBgColor = "#1a3a2a",
        string? prefLevel = null,
        string? prefStyles = null,
        string? prefGoal = null,
        string loginMethod = "username_password",
        string? pin = null,
        string? preferredUsername = null,
        string? customPassword = null,
        bool parentalConsentAcknowledged = true,
        string ipAddress = "127.0.0.1",
        string? userAgent = "xunit",
        string? correlationId = null)
        => new CreateChildCommand(
            ParentUserId: parentUserId,
            ParentTenantId: parentTenantId,
            FullName: fullName,
            Grade: grade,
            Gender: gender,
            BirthYear: birthYear,
            BirthMonth: birthMonth,
            CurriculumType: curriculumType,
            SchoolName: schoolName,
            AvatarEmoji: avatarEmoji,
            AvatarBgColor: avatarBgColor,
            PrefLevel: prefLevel,
            PrefStyles: prefStyles,
            PrefGoal: prefGoal,
            LoginMethod: loginMethod,
            Pin: pin,
            PreferredUsername: preferredUsername,
            CustomPassword: customPassword,
            ParentalConsentAcknowledged: parentalConsentAcknowledged,
            IpAddress: ipAddress,
            UserAgent: userAgent,
            CorrelationId: correlationId ?? Guid.NewGuid().ToString("D"));
}
