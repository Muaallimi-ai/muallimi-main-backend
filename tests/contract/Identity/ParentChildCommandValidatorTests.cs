using System;
using System.Linq;
using Muallimi.Application.Identity.Commands;
using Xunit;

namespace Muallimi.MainBackend.Tests.Contract.Identity;

/// <summary>
/// Parent-children command validators accept the full KG1 (-1), KG2 (0),
/// and Grade 1..12 range (matches the frontend grade catalogue), and the
/// update-path skips the birthday range check when the caller submits
/// null or <see cref="DateTime.MinValue"/> (the legacy-row sentinel).
/// </summary>
public class ParentChildCommandValidatorTests
{
    private static CreateChildCommand MakeCreate(int grade, DateTime? birthday = null)
        => new(
            ParentUserId: Guid.NewGuid(),
            ParentTenantId: Guid.NewGuid(),
            FullName: "الطفل",
            FullNameEn: null,
            Grade: grade,
            Gender: "male",
            Birthday: birthday ?? new DateTime(2016, 1, 1),
            PreferredUsername: null,
            CustomPassword: null,
            PasswordLocale: "ar",
            IpAddress: "127.0.0.1",
            UserAgent: "xunit",
            CorrelationId: Guid.NewGuid().ToString("D"));

    private static UpdateChildCommand MakeUpdate(int? grade = null, DateTime? birthday = null)
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
            CorrelationId: Guid.NewGuid().ToString("D"));

    [Theory]
    [InlineData(-1)]  // KG1
    [InlineData(0)]   // KG2
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    public void Create_Accepts_Grades_Minus1_Through_12(int grade)
    {
        var validator = new CreateChildCommandValidator();
        var errors = validator.Validate(MakeCreate(grade));
        Assert.DoesNotContain(errors, e => e.Code == "grade_invalid");
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(13)]
    [InlineData(99)]
    public void Create_Rejects_Out_Of_Range_Grade(int grade)
    {
        var validator = new CreateChildCommandValidator();
        var errors = validator.Validate(MakeCreate(grade));
        Assert.Contains(errors, e => e.Code == "grade_invalid");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(12)]
    public void Update_Accepts_Grades_Minus1_Through_12(int grade)
    {
        var validator = new UpdateChildCommandValidator();
        var errors = validator.Validate(MakeUpdate(grade: grade));
        Assert.DoesNotContain(errors, e => e.Code == "grade_invalid");
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(13)]
    public void Update_Rejects_Out_Of_Range_Grade(int grade)
    {
        var validator = new UpdateChildCommandValidator();
        var errors = validator.Validate(MakeUpdate(grade: grade));
        Assert.Contains(errors, e => e.Code == "grade_invalid");
    }

    [Fact]
    public void Update_Null_Birthday_Does_Not_Trigger_BirthdayInvalid()
    {
        var validator = new UpdateChildCommandValidator();
        var errors = validator.Validate(MakeUpdate(grade: 3, birthday: null));
        Assert.DoesNotContain(errors, e => e.Code == "birthday_invalid");
    }

    [Fact]
    public void Update_DefaultDateTime_Birthday_Does_Not_Trigger_BirthdayInvalid()
    {
        // Legacy rows where birthday was never persisted arrive as
        // DateTime.MinValue after the client round-trips the bogus GET.
        var validator = new UpdateChildCommandValidator();
        var errors = validator.Validate(MakeUpdate(grade: 3, birthday: default(DateTime)));
        Assert.DoesNotContain(errors, e => e.Code == "birthday_invalid");
    }

    [Fact]
    public void Update_Real_Future_Birthday_Still_Rejected()
    {
        var validator = new UpdateChildCommandValidator();
        var future = DateTime.UtcNow.Date.AddYears(1);
        var errors = validator.Validate(MakeUpdate(birthday: future));
        Assert.Contains(errors, e => e.Code == "birthday_invalid");
    }

    [Fact]
    public void Update_Real_Birthday_Older_Than_25_Years_Still_Rejected()
    {
        var validator = new UpdateChildCommandValidator();
        var old = DateTime.UtcNow.Date.AddYears(-40);
        var errors = validator.Validate(MakeUpdate(birthday: old));
        Assert.Contains(errors, e => e.Code == "birthday_invalid");
    }
}
