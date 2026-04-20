namespace Muallimi.Domain.Identity.Enums;

public enum LoginOutcome
{
    Success = 1,
    InvalidCredentials = 2,
    UserNotFound = 3,
    AccountLocked = 4,
    AccountSuspended = 5,
    TwoFactorRequired = 6,
    TwoFactorFailed = 7,
}
