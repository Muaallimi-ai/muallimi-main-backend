using System;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.Domain.Tests.Identity;

/// <summary>
/// T057 — Domain tests for <see cref="User"/> state machine transitions.
///
/// Pins the status transitions described in
/// <c>specs/009-identity-auth/data-model.md</c>:
///   PendingEmailVerification → Active (via VerifyEmail)
///   Active → Locked (via failed logins past threshold)
///   Locked → Active (via successful login after lockout elapses,
///                    or via CompletePasswordReset)
///   Active → Suspended → Active (via Suspend / Unsuspend)
///   Active → PasswordResetRequired → Active (via RequirePasswordReset /
///                                            CompletePasswordReset)
///   Any non-Archived → Archived (terminal)
/// </summary>
public class UserStateMachineTests
{
    private static User NewPersonal() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        AccountType = AccountType.Personal,
        Email = "user@example.com",
        FullName = "User",
        Status = UserStatus.PendingEmailVerification,
    };

    [Fact]
    public void VerifyEmail_Transitions_Pending_To_Active()
    {
        var u = NewPersonal();
        u.VerifyEmail();

        Assert.Equal(UserStatus.Active, u.Status);
        Assert.True(u.EmailVerified);
        Assert.NotNull(u.EmailVerifiedAt);
    }

    [Fact]
    public void VerifyEmail_Rejects_If_Not_Pending()
    {
        var u = NewPersonal();
        u.Status = UserStatus.Active;
        Assert.Throws<InvalidOperationException>(() => u.VerifyEmail());
    }

    [Fact]
    public void Failed_Logins_Lock_After_Threshold()
    {
        var u = NewPersonal();
        u.Status = UserStatus.Active;
        for (var i = 0; i < 5; i++)
        {
            u.RegisterFailedLogin(5, TimeSpan.FromMinutes(15));
        }
        Assert.Equal(UserStatus.Locked, u.Status);
        Assert.NotNull(u.LockoutEnd);
        Assert.Equal(5, u.FailedLoginAttempts);
    }

    [Fact]
    public void Below_Threshold_Keeps_Status_Active()
    {
        var u = NewPersonal();
        u.Status = UserStatus.Active;
        u.RegisterFailedLogin(5, TimeSpan.FromMinutes(15));
        Assert.Equal(UserStatus.Active, u.Status);
        Assert.Null(u.LockoutEnd);
    }

    [Fact]
    public void Successful_Login_After_Lockout_Elapses_Reactivates()
    {
        var u = NewPersonal();
        u.Status = UserStatus.Locked;
        u.LockoutEnd = DateTime.UtcNow.AddMinutes(-1);
        u.FailedLoginAttempts = 5;

        u.MarkSuccessfulLogin("127.0.0.1");

        Assert.Equal(UserStatus.Active, u.Status);
        Assert.Null(u.LockoutEnd);
        Assert.Equal(0, u.FailedLoginAttempts);
        Assert.NotNull(u.LastLoginAt);
    }

    [Fact]
    public void Successful_Login_Rejects_While_Still_Locked()
    {
        var u = NewPersonal();
        u.Status = UserStatus.Locked;
        u.LockoutEnd = DateTime.UtcNow.AddMinutes(15);

        Assert.Throws<InvalidOperationException>(() => u.MarkSuccessfulLogin("127.0.0.1"));
    }

    [Fact]
    public void Successful_Login_Rejects_When_Archived()
    {
        var u = NewPersonal();
        u.Status = UserStatus.Archived;
        Assert.Throws<InvalidOperationException>(() => u.MarkSuccessfulLogin("127.0.0.1"));
    }

    [Fact]
    public void Active_Can_Be_Suspended_And_Unsuspended()
    {
        var u = NewPersonal();
        u.Status = UserStatus.Active;

        u.Suspend();
        Assert.Equal(UserStatus.Suspended, u.Status);

        u.Unsuspend();
        Assert.Equal(UserStatus.Active, u.Status);
    }

    [Fact]
    public void Unsuspend_Rejects_If_Not_Suspended()
    {
        var u = NewPersonal();
        u.Status = UserStatus.Active;
        Assert.Throws<InvalidOperationException>(() => u.Unsuspend());
    }

    [Fact]
    public void Suspend_Rejects_Archived_User()
    {
        var u = NewPersonal();
        u.Status = UserStatus.Archived;
        Assert.Throws<InvalidOperationException>(() => u.Suspend());
    }

    [Fact]
    public void RequirePasswordReset_Flips_Status_And_Flag()
    {
        var u = NewPersonal();
        u.Status = UserStatus.Active;

        u.RequirePasswordReset();

        Assert.Equal(UserStatus.PasswordResetRequired, u.Status);
        Assert.True(u.RequiresPasswordReset);
    }

    [Fact]
    public void RequirePasswordReset_Rejects_Archived_User()
    {
        var u = NewPersonal();
        u.Status = UserStatus.Archived;
        Assert.Throws<InvalidOperationException>(() => u.RequirePasswordReset());
    }

    [Fact]
    public void CompletePasswordReset_From_Required_Returns_To_Active()
    {
        var u = NewPersonal();
        u.Status = UserStatus.PasswordResetRequired;
        u.RequiresPasswordReset = true;

        u.CompletePasswordReset("new-hash");

        Assert.Equal(UserStatus.Active, u.Status);
        Assert.False(u.RequiresPasswordReset);
        Assert.Equal("new-hash", u.PasswordHash);
        Assert.NotNull(u.PasswordChangedAt);
    }

    [Fact]
    public void CompletePasswordReset_From_Locked_Unlocks_User()
    {
        var u = NewPersonal();
        u.Status = UserStatus.Locked;
        u.LockoutEnd = DateTime.UtcNow.AddHours(1);
        u.FailedLoginAttempts = 5;

        u.CompletePasswordReset("new-hash");

        Assert.Equal(UserStatus.Active, u.Status);
        Assert.Null(u.LockoutEnd);
        Assert.Equal(0, u.FailedLoginAttempts);
    }

    [Fact]
    public void CompletePasswordReset_Rejects_Archived_User()
    {
        var u = NewPersonal();
        u.Status = UserStatus.Archived;
        Assert.Throws<InvalidOperationException>(() => u.CompletePasswordReset("hash"));
    }

    [Fact]
    public void Archive_Is_Terminal()
    {
        var u = NewPersonal();
        u.Status = UserStatus.Active;

        u.Archive();

        Assert.Equal(UserStatus.Archived, u.Status);
        Assert.NotNull(u.DeletedAt);

        // All subsequent transitions refuse.
        Assert.Throws<InvalidOperationException>(() => u.Suspend());
        Assert.Throws<InvalidOperationException>(() => u.RequirePasswordReset());
        Assert.Throws<InvalidOperationException>(() => u.CompletePasswordReset("hash"));
        Assert.Throws<InvalidOperationException>(() => u.MarkSuccessfulLogin("127.0.0.1"));
    }

    [Fact]
    public void Personal_Account_Requires_Email()
    {
        var u = NewPersonal();
        u.Email = null;
        Assert.Throws<InvalidOperationException>(() => u.AssertAccountTypeInvariants());
    }

    [Fact]
    public void Managed_Account_Requires_Username_And_Manager()
    {
        var u = new User
        {
            AccountType = AccountType.Managed,
            FullName = "Student",
            Username = null,
        };
        Assert.Throws<InvalidOperationException>(() => u.AssertAccountTypeInvariants());

        u.Username = "s01";
        u.ManagedByUserId = null;
        Assert.Throws<InvalidOperationException>(() => u.AssertAccountTypeInvariants());

        u.ManagedByUserId = Guid.NewGuid();
        u.AssertAccountTypeInvariants(); // Should not throw.
    }
}
