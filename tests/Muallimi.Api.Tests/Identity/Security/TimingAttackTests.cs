using System;
using System.Diagnostics;
using System.Linq;
using Muallimi.Application.Identity.Services;
using Xunit;

namespace Muallimi.Api.Tests.Identity.Security;

/// <summary>
/// T061 — Integration test for Phase 9 Identity timing-attack invariance.
///
/// SC-009: the unknown-email login path (<c>VerifyWithDummyFallback</c>
/// with a null hash) and the known-email-wrong-password path
/// (<c>VerifyWithDummyFallback</c> with a real hash + wrong plaintext)
/// MUST take ≥ 95% overlapping latency. Equivalently, their median
/// wall-clock times must be within a small envelope of each other.
///
/// Implementation check — both paths call into BCrypt with work factor
/// 12. We measure medians across a warm sample to avoid JIT and
/// first-call noise, then assert the ratio is within tolerance.
/// </summary>
public class TimingAttackTests
{
    private const int WarmupIterations = 3;
    private const int SampledIterations = 9;

    [Fact]
    public void Unknown_Email_And_Wrong_Password_Have_Overlapping_Latency()
    {
        var svc = new BCryptPasswordService();
        var realHash = svc.Hash("correct-horse-battery-staple");

        // Warm up BCrypt JIT / internal caches.
        for (var i = 0; i < WarmupIterations; i++)
        {
            _ = svc.VerifyWithDummyFallback("warmup", realHash);
            _ = svc.VerifyWithDummyFallback("warmup", hash: null);
        }

        var unknownEmailTicks = new long[SampledIterations];
        var wrongPasswordTicks = new long[SampledIterations];

        // Interleave to reduce any systemic drift (GC pauses, scheduler).
        for (var i = 0; i < SampledIterations; i++)
        {
            unknownEmailTicks[i] = Measure(() =>
                svc.VerifyWithDummyFallback("any-password", hash: null));
            wrongPasswordTicks[i] = Measure(() =>
                svc.VerifyWithDummyFallback("wrong-password", realHash));
        }

        var unknownMedian = Median(unknownEmailTicks);
        var wrongMedian = Median(wrongPasswordTicks);

        // Both branches must have returned false — the service must not
        // succeed on either path.
        Assert.False(svc.VerifyWithDummyFallback("any-password", hash: null));
        Assert.False(svc.VerifyWithDummyFallback("wrong-password", realHash));

        // Ratio within [0.7, 1.43] covers ≥ 95% overlap in practice with
        // BCrypt's work factor 12 (≥ ~200ms each) and CI jitter.
        // BCrypt's own runtime is deterministic modulo OS scheduling;
        // the wider envelope tolerates shared-CI hosts.
        var ratio = (double)wrongMedian / unknownMedian;
        Assert.InRange(ratio, 0.7, 1.43);

        // Both medians must be non-trivial — a regression that skipped
        // BCrypt on one path would produce microsecond times.
        Assert.True(unknownMedian > TimeSpan.FromMilliseconds(10).Ticks,
            $"Unknown-email path too fast: {TimeSpan.FromTicks(unknownMedian).TotalMilliseconds:F1}ms");
        Assert.True(wrongMedian > TimeSpan.FromMilliseconds(10).Ticks,
            $"Wrong-password path too fast: {TimeSpan.FromTicks(wrongMedian).TotalMilliseconds:F1}ms");
    }

    [Fact]
    public void Empty_Password_Still_Runs_Dummy_Hash()
    {
        var svc = new BCryptPasswordService();
        var realHash = svc.Hash("correct-horse-battery-staple");

        // Warm up.
        _ = svc.VerifyWithDummyFallback("", hash: null);
        _ = svc.VerifyWithDummyFallback("", realHash);

        var emptyNoHashTicks = Measure(() => svc.VerifyWithDummyFallback("", hash: null));
        var emptyWithHashTicks = Measure(() => svc.VerifyWithDummyFallback("", realHash));

        // Both branches must return false without short-circuiting.
        Assert.False(svc.VerifyWithDummyFallback("", hash: null));
        Assert.False(svc.VerifyWithDummyFallback("", realHash));

        // Both paths must execute real BCrypt work.
        Assert.True(emptyNoHashTicks > TimeSpan.FromMilliseconds(5).Ticks);
        Assert.True(emptyWithHashTicks > TimeSpan.FromMilliseconds(5).Ticks);
    }

    private static long Measure(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.ElapsedTicks;
    }

    private static long Median(long[] samples)
    {
        var sorted = samples.OrderBy(x => x).ToArray();
        return sorted[sorted.Length / 2];
    }
}
