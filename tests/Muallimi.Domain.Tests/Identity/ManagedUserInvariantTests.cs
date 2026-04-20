using System;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Xunit;

namespace Muallimi.Domain.Tests.Identity;

/// <summary>
/// T085 — Domain invariant tests for Managed users. Pins that Managed
/// accounts must carry a <see cref="User.Username"/> and a
/// <see cref="User.ManagedByUserId"/>, and that <see cref="User.Email"/>
/// is not required (and must not drive the Personal-account email rule
/// by accident).
/// </summary>
public class ManagedUserInvariantTests
{
    [Fact]
    public void Managed_User_Requires_Username()
    {
        var u = new User
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            AccountType = AccountType.Managed,
            ManagedByUserId = Guid.NewGuid(),
            Username = null,
            FullName = "طفل",
        };
        Assert.Throws<InvalidOperationException>(u.AssertAccountTypeInvariants);
    }

    [Fact]
    public void Managed_User_Requires_ManagedByUserId()
    {
        var u = new User
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            AccountType = AccountType.Managed,
            ManagedByUserId = null,
            Username = "child.2015.001",
            FullName = "طفل",
        };
        Assert.Throws<InvalidOperationException>(u.AssertAccountTypeInvariants);
    }

    [Fact]
    public void Managed_User_Without_Email_Is_Valid()
    {
        var u = new User
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            AccountType = AccountType.Managed,
            ManagedByUserId = Guid.NewGuid(),
            Username = "child.2015.001",
            Email = null,
            FullName = "طفل",
        };
        u.AssertAccountTypeInvariants();
    }

    [Fact]
    public void Personal_User_Still_Requires_Email()
    {
        var u = new User
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            AccountType = AccountType.Personal,
            Email = null,
            FullName = "والد",
        };
        Assert.Throws<InvalidOperationException>(u.AssertAccountTypeInvariants);
    }
}
