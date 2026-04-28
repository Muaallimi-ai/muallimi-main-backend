using System;
using System.Collections.Generic;

namespace Muallimi.Application.Identity.Services;

/// <summary>
/// Add-child redesign security non-negotiable #3: enforce a weak-PIN
/// blocklist at the moment a PIN is set (NOT on login). Blocks the
/// classic 1234/0000/1111 family, all sequential ascending/descending
/// runs, and the child's birth year when known.
/// </summary>
public interface IWeakPinBlocklist
{
    /// <returns><c>true</c> if the PIN should be rejected.</returns>
    bool IsWeak(string pin, int? childBirthYear = null);
}

public sealed class WeakPinBlocklist : IWeakPinBlocklist
{
    private static readonly HashSet<string> CommonWeak = new(StringComparer.Ordinal)
    {
        // Same-digit
        "0000","1111","2222","3333","4444","5555","6666","7777","8888","9999",
        // Sequential ascending
        "0123","1234","2345","3456","4567","5678","6789",
        // Sequential descending
        "9876","8765","7654","6543","5432","4321","3210",
        // Common low-entropy combos (popular leaks)
        "1212","2121","1010","0101","6969","4242","1357","2468"
    };

    public bool IsWeak(string pin, int? childBirthYear = null)
    {
        if (string.IsNullOrEmpty(pin)) return true;
        if (CommonWeak.Contains(pin)) return true;
        if (childBirthYear is { } y && pin == y.ToString("D4")) return true;
        return false;
    }
}
