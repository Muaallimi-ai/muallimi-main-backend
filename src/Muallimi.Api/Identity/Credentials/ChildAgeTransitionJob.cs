using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Muallimi.Domain.Identity.Entities;
using Muallimi.Domain.Identity.Enums;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Api.Identity.Credentials;

/// <summary>
/// Phase 9 Phase 4 daily job that fires the credential-tier upgrade
/// prompt to parents on their child's 8th and 13th birthdays.
///
/// Query (per <c>specs/009-identity-auth</c> + Phase 9 memory):
///   <code>
///   FROM identity_users u
///   JOIN student_profiles sp ON sp.user_id = u.id
///   WHERE u.account_type = Managed
///     AND u.age_transition_notified_at IS NULL
///     AND [computed_age_from_sp.birthday] IN (8, 13)
///     AND u.login_method = 'profile_switch_only' (for 8) OR 'pin' (for 13)
///   </code>
/// School-managed students (manager.AccountType != Personal) are
/// excluded — schools handle credentials at enrollment, not by
/// birthday transitions.
///
/// Idempotent: <c>AgeTransitionNotifiedAt</c> is stamped on the child
/// row in the same transaction as the notification fan-out, so a
/// crashed-and-resumed job never double-notifies. A child whose
/// parent doesn't act keeps <c>LoginMethod = profile_switch_only</c>
/// (or <c>pin</c>) — the parent dashboard's child card surfaces the
/// upgrade affordance permanently until they click it.
///
/// Mirrors <c>Muallimi.Api.Compliance.DataRetention.DataRetentionHostedService</c>.
/// </summary>
public sealed class ChildAgeTransitionJob : BackgroundService
{
    private const int PinEligibleAge = 8;
    private const int PasswordEligibleAge = 13;

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ChildAgeTransitionJob> _logger;
    private readonly TimeSpan _interval;

    public ChildAgeTransitionJob(
        IServiceScopeFactory scopes,
        ILogger<ChildAgeTransitionJob> logger,
        IHostEnvironment environment)
    {
        _scopes = scopes;
        _logger = logger;
        // Production: 24h. Development: 60min so smoke scripts can observe
        // a run end-to-end without waiting overnight. Same cadence policy
        // as DataRetentionHostedService.
        _interval = environment.IsDevelopment() ? TimeSpan.FromMinutes(60) : TimeSpan.FromHours(24);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay so DI / migrations settle before the first run.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "child_age_transition_job.run.failed");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MuallimiDbContext>();
        var notifier = scope.ServiceProvider.GetRequiredService<IChildCredentialNotifier>();

        // Pull every Managed account that hasn't been notified yet, joined
        // with the StudentProfile that holds the birthday. Filtering by
        // age happens in-memory (DateOnly arithmetic doesn't translate
        // cleanly to SQL across providers). Result set is bounded by
        // count of children added in the last ~13 years per tenant —
        // small enough to enumerate.
        var candidates = await (
            from u in db.IdentityUsers.IgnoreQueryFilters()
            join sp in db.StudentProfiles.IgnoreQueryFilters()
                on u.Id equals sp.UserId
            where u.AccountType == AccountType.Managed
                  && u.AgeTransitionNotifiedAt == null
                  && u.Status != UserStatus.Archived
                  && sp.Birthday != null
                  && (u.LoginMethod == LoginMethods.ProfileSwitchOnly || u.LoginMethod == LoginMethods.Pin)
            select new { User = u, Birthday = sp.Birthday!.Value }
        ).ToListAsync(ct).ConfigureAwait(false);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        int firedPin = 0, firedPassword = 0;
        foreach (var row in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var age = ComputeAge(row.Birthday, today);
            var correlationId = Guid.NewGuid().ToString("D");

            if (age == PinEligibleAge && row.User.LoginMethod == LoginMethods.ProfileSwitchOnly)
            {
                await NotifyAndStampAsync(
                    db, notifier, row.User, correlationId,
                    n => n.NotifyBirthdayPinEligibleAsync(row.User, correlationId, ct),
                    ct).ConfigureAwait(false);
                firedPin++;
            }
            else if (age == PasswordEligibleAge && row.User.LoginMethod == LoginMethods.Pin)
            {
                await NotifyAndStampAsync(
                    db, notifier, row.User, correlationId,
                    n => n.NotifyBirthdayPasswordEligibleAsync(row.User, correlationId, ct),
                    ct).ConfigureAwait(false);
                firedPassword++;
            }
        }

        _logger.LogInformation(
            "child_age_transition_job.run candidates={Candidates} fired_pin={Pin} fired_password={Password}",
            candidates.Count, firedPin, firedPassword);
    }

    private async Task NotifyAndStampAsync(
        MuallimiDbContext db,
        IChildCredentialNotifier notifier,
        User child,
        string correlationId,
        Func<IChildCredentialNotifier, Task> notify,
        CancellationToken ct)
    {
        try
        {
            await notify(notifier).ConfigureAwait(false);
            // Stamp the marker AFTER successful notification so a transient
            // failure leaves the child eligible for the next job tick.
            child.AgeTransitionNotifiedAt = DateTime.UtcNow;
            child.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "child_age_transition_job.notify.failed user={UserId} correlation_id={CorrelationId}",
                child.Id, correlationId);
        }
    }

    private static int ComputeAge(DateOnly birthday, DateOnly today)
    {
        var age = today.Year - birthday.Year;
        if (birthday > today.AddYears(-age)) age--;
        return age;
    }
}
