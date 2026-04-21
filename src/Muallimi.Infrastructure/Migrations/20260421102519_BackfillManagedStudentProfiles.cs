using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Muallimi.Infrastructure.Migrations
{
    /// <summary>
    /// Backfills <c>student_profiles</c> rows for managed child users (role =
    /// "student") that were created before <c>UserManagementService.CreateChildAsync</c>
    /// started writing the profile row synchronously.
    ///
    /// A missing StudentProfile row breaks the Phase 3 student experience:
    /// the JWT's <c>profile_ids.student</c> claim is null → frontend
    /// <c>resolveStudentIdentity()</c> returns null → every student surface
    /// (home, progress, leaderboard, study, tutor, mock-test, homework-help,
    /// whiteboard) errors with <c>missing_identity</c> and all tiles render
    /// as locked skeletons.
    ///
    /// The INSERT is idempotent: the <c>NOT EXISTS</c> clause skips any user
    /// that already has a profile. Safe to re-run across staging/prod.
    /// Defaults mirror <c>UserManagementService.CreateChildAsync</c>
    /// (curriculum "MOE-EG", grade "1", free plan, pending consent).
    /// </summary>
    public partial class BackfillManagedStudentProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
INSERT INTO student_profiles (
    id, tenant_id, user_id, display_name, avatar_reference,
    curriculum_type, grade, preferred_language, rtl_override,
    plan_tier, subjects_enrolled, consent_state,
    birthday, gender, created_at, updated_at)
SELECT
    gen_random_uuid(),
    u.tenant_id,
    u.id,
    COALESCE(NULLIF(u.full_name, ''), u.username),
    NULL,
    'MOE-EG',
    '1',
    COALESCE(u.locale, 'ar'),
    NULL,
    'free',
    '[]',
    'pending',
    NULL,
    NULL,
    now(),
    now()
FROM identity_users u
JOIN identity_user_roles ur
    ON ur.user_id = u.id
   AND ur.revoked_at IS NULL
JOIN identity_roles r
    ON r.id = ur.role_id
   AND r.name = 'student'
WHERE NOT EXISTS (
    SELECT 1
    FROM student_profiles sp
    WHERE sp.user_id = u.id
);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversing a data backfill is destructive — the identifying
            // information (which profiles were backfilled vs. legitimately
            // created) is lost after the Up. Intentionally a no-op so a
            // rollback doesn't nuke unrelated StudentProfile rows.
        }
    }
}
