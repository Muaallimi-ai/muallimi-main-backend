using System;

namespace Muallimi.Application.Identity.Services;

/// <summary>
/// T088 — Generates the 3-word-2-digit phrase passwords shown exactly
/// once on child-account creation. Format: <c>{word}-{word}-{word}-NN</c>.
/// Locale <c>ar</c> picks Arabic words (the default for managed students
/// per the spec), <c>en</c> picks short memorable English words. The
/// lists are intentionally small + child-friendly — easy to read aloud
/// over the phone or write on a printout.
/// </summary>
public interface IChildPasswordGenerator
{
    /// <summary>
    /// Returns a plaintext phrase password. The caller is responsible
    /// for hashing it via <see cref="IPasswordService.Hash"/> before
    /// persisting, and for surfacing the plaintext to the parent exactly
    /// once in a <c>ChildCredentialsOnce</c> envelope.
    /// </summary>
    string Generate(string locale);
}

public sealed class ChildPasswordGenerator : IChildPasswordGenerator
{
    private static readonly string[] ArabicWords =
    {
        "قمر", "نهر", "شمس", "نجم", "غيث", "ورد", "سحاب", "بحر", "زهر",
        "فجر", "ضوء", "سماء", "نور", "ريح", "جبل", "حقل", "عصفور", "فراشة",
        "كوكب", "ربيع", "غابة", "نهار", "ساحل", "سنبلة", "مرجان", "لؤلؤ",
    };

    private static readonly string[] EnglishWords =
    {
        "moon", "river", "sun", "star", "cloud", "rose", "sea", "dawn",
        "light", "sky", "wind", "hill", "field", "bird", "butterfly",
        "planet", "spring", "forest", "day", "shore", "wheat", "coral",
        "pearl", "meadow", "falcon", "eagle", "lantern",
    };

    private readonly Random _random;

    public ChildPasswordGenerator() : this(new Random()) { }

    // Seeded constructor for deterministic test runs.
    public ChildPasswordGenerator(Random random) { _random = random; }

    public string Generate(string locale)
    {
        var bank = locale == "en" ? EnglishWords : ArabicWords;
        var a = bank[_random.Next(bank.Length)];
        string b;
        string c;
        do { b = bank[_random.Next(bank.Length)]; } while (b == a);
        do { c = bank[_random.Next(bank.Length)]; } while (c == a || c == b);
        var number = _random.Next(10, 100); // 2-digit
        return $"{a}-{b}-{c}-{number}";
    }
}
