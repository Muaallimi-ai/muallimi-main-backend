using System;
using System.Linq;
using Muallimi.Application.Identity.Commands;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity;

/// <summary>
/// Parent-children command validators after the add-child redesign.
/// CreateChildCommand now takes BirthYear+BirthMonth (not Birthday),
/// avatar/curriculum/preferences inputs, an explicit login method, and
/// the required ParentalConsentAcknowledged flag. UpdateChildCommand
/// still uses the legacy Birthday DateTime so existing PATCH callers
/// keep working.
/// </summary>
public class ParentChildCommandValidatorTests
{
    private static CreateChildCommand MakeCreate(
        int grade = 3,
        int birthYear = 2017,
        int birthMonth = 6,
        bool consent = true,
        string loginMethod = "username_password",
        string? pin = null,
        string? username = null,
        string? password = null)
        => new(
            ParentUserId: Guid.NewGuid(),
            ParentTenantId: Guid.NewGuid(),
            FullName: "الطفل",
            Grade: grade,
            Gender: "male",
            BirthYear: birthYear,
            BirthMonth: birthMonth,
            CurriculumType: "Moe",
            SchoolName: null,
            AvatarEmoji: "🐸",
            AvatarBgColor: "#1a3a2a",
            PrefLevel: null,
            PrefStyles: null,
            PrefGoal: null,
            LoginMethod: loginMethod,
            Pin: pin,
            PreferredUsername: username,
            CustomPassword: password,
            ParentalConsentAcknowledged: consent,
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D"));

    private static UpdateChildCommand MakeUpdate(int? grade = null, DateTime? birthday = null, string? username = null)
        => new(
            ParentUserId: Guid.NewGuid(),
            ParentTenantId: Guid.NewGuid(),
            ChildUserId: Guid.NewGuid(),
            FullName: null,
            FullNameEn: null,
            Grade: grade,
            Gender: null,
            Birthday: birthday,
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D"),
            Username: username);

    [Theory]
    [InlineData(-1)]  // KG1
    [InlineData(0)]   // KG2
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    public void Create_Accepts_Grades_Minus1_Through_12(int grade)
    {
        var errors = new CreateChildCommandValidator().Validate(MakeCreate(grade));
        Assert.DoesNotContain(errors, e => e.Code == "grade_invalid");
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(13)]
    [InlineData(99)]
    public void Create_Rejects_Out_Of_Range_Grade(int grade)
    {
        var errors = new CreateChildCommandValidator().Validate(MakeCreate(grade));
        Assert.Contains(errors, e => e.Code == "grade_invalid");
    }

    [Fact]
    public void Create_Rejects_Missing_Parental_Consent()
    {
        var errors = new CreateChildCommandValidator().Validate(MakeCreate(consent: false));
        Assert.Contains(errors, e => e.Code == "parental_consent_required");
    }

    [Fact]
    public void Create_Accepts_With_Parental_Consent()
    {
        var errors = new CreateChildCommandValidator().Validate(MakeCreate(consent: true));
        Assert.DoesNotContain(errors, e => e.Code == "parental_consent_required");
    }

    [Fact]
    public void Create_Pin_Method_Requires_Four_Digit_Pin()
    {
        var bad = new CreateChildCommandValidator().Validate(MakeCreate(loginMethod: "pin", pin: "12"));
        Assert.Contains(bad, e => e.Code == "pin_invalid");
        var ok = new CreateChildCommandValidator().Validate(MakeCreate(loginMethod: "pin", pin: "8421"));
        Assert.DoesNotContain(ok, e => e.Code == "pin_invalid");
    }

    [Theory]
    [InlineData("ab")]              // too short
    [InlineData("aaaabbbbccccddddeeeef")] // too long (>20)
    [InlineData("bad-name")]        // hyphen no longer allowed
    [InlineData("bad.name")]        // dot no longer allowed
    public void Create_Rejects_Invalid_Username(string username)
    {
        var errors = new CreateChildCommandValidator().Validate(
            MakeCreate(loginMethod: "username_password", username: username));
        Assert.Contains(errors, e => e.Field == "preferredUsername");
    }

    [Theory]
    [InlineData("good_name")]
    [InlineData("Mohamed_2012_001")]
    [InlineData("user_42")]
    public void Create_Accepts_Valid_Username(string username)
    {
        var errors = new CreateChildCommandValidator().Validate(
            MakeCreate(loginMethod: "username_password", username: username));
        Assert.DoesNotContain(errors, e => e.Field == "preferredUsername");
    }

    [Fact]
    public void Create_Rejects_Invalid_LoginMethod()
    {
        var errors = new CreateChildCommandValidator().Validate(MakeCreate(loginMethod: "avatar_only"));
        Assert.Contains(errors, e => e.Code == "login_method_invalid");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(12)]
    public void Update_Accepts_Grades_Minus1_Through_12(int grade)
    {
        var errors = new UpdateChildCommandValidator().Validate(MakeUpdate(grade: grade));
        Assert.DoesNotContain(errors, e => e.Code == "grade_invalid");
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(13)]
    public void Update_Rejects_Out_Of_Range_Grade(int grade)
    {
        var errors = new UpdateChildCommandValidator().Validate(MakeUpdate(grade: grade));
        Assert.Contains(errors, e => e.Code == "grade_invalid");
    }

    [Fact]
    public void Update_Null_Birthday_Does_Not_Trigger_BirthdayInvalid()
    {
        var errors = new UpdateChildCommandValidator().Validate(MakeUpdate(grade: 3, birthday: null));
        Assert.DoesNotContain(errors, e => e.Code == "birthday_invalid");
    }

    [Fact]
    public void Update_DefaultDateTime_Birthday_Does_Not_Trigger_BirthdayInvalid()
    {
        var errors = new UpdateChildCommandValidator().Validate(MakeUpdate(grade: 3, birthday: default(DateTime)));
        Assert.DoesNotContain(errors, e => e.Code == "birthday_invalid");
    }

    [Fact]
    public void Update_Real_Future_Birthday_Still_Rejected()
    {
        var future = DateTime.UtcNow.Date.AddYears(1);
        var errors = new UpdateChildCommandValidator().Validate(MakeUpdate(birthday: future));
        Assert.Contains(errors, e => e.Code == "birthday_invalid");
    }

    [Fact]
    public void Update_Real_Birthday_Older_Than_25_Years_Still_Rejected()
    {
        var old = DateTime.UtcNow.Date.AddYears(-40);
        var errors = new UpdateChildCommandValidator().Validate(MakeUpdate(birthday: old));
        Assert.Contains(errors, e => e.Code == "birthday_invalid");
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("hyphen-not-ok")]
    public void Update_Rejects_Invalid_Username(string username)
    {
        var errors = new UpdateChildCommandValidator().Validate(MakeUpdate(grade: 3, username: username));
        Assert.Contains(errors, e => e.Field == "username");
    }

    [Fact]
    public void Update_Accepts_Valid_Username()
    {
        var errors = new UpdateChildCommandValidator().Validate(MakeUpdate(grade: 3, username: "valid_name_42"));
        Assert.DoesNotContain(errors, e => e.Field == "username");
    }
}
