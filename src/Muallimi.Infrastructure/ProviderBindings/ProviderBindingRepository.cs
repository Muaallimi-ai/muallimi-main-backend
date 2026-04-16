using Microsoft.EntityFrameworkCore;
using Muallimi.Domain.ProviderBindings;
using Muallimi.Infrastructure.Persistence;

namespace Muallimi.Infrastructure.ProviderBindings;

/// <summary>
/// T089 (US5) — Repository over <see cref="ProviderAdapterBinding"/>. Enforces
/// the "exactly one active binding per (capability, environment, curriculum_scope)"
/// invariant in application code before the DB catches it (filtered unique
/// index) so operators get a descriptive error instead of a raw constraint
/// violation. Also validates that every entry in a binding's fallback chain
/// targets a binding of the same capability.
/// </summary>
public interface IProviderBindingRepository
{
    Task<ProviderAdapterBinding?> GetAsync(Guid bindingId, CancellationToken ct = default);

    Task<IReadOnlyList<ProviderAdapterBinding>> ListAsync(
        string? capability,
        string? environment,
        string? curriculumScope,
        CancellationToken ct = default);

    Task<ProviderAdapterBinding?> ResolveActiveAsync(
        string capability,
        string environment,
        string? curriculumScope,
        CancellationToken ct = default);

    Task<IReadOnlyList<ProviderAdapterBinding>> ResolveFallbackChainAsync(
        Guid primaryBindingId,
        CancellationToken ct = default);

    Task AddAsync(ProviderAdapterBinding binding, CancellationToken ct = default);

    Task ValidateFallbackChainAsync(ProviderAdapterBinding binding, CancellationToken ct = default);

    Task ActivateAsync(ProviderAdapterBinding target, CancellationToken ct = default);

    Task DeactivateAsync(ProviderAdapterBinding target, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}

public class ProviderBindingRepository : IProviderBindingRepository
{
    private readonly MuallimiDbContext _db;

    public ProviderBindingRepository(MuallimiDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public Task<ProviderAdapterBinding?> GetAsync(Guid bindingId, CancellationToken ct = default)
        => _db.ProviderAdapterBindings.FirstOrDefaultAsync(b => b.BindingId == bindingId, ct);

    public async Task<IReadOnlyList<ProviderAdapterBinding>> ListAsync(
        string? capability,
        string? environment,
        string? curriculumScope,
        CancellationToken ct = default)
    {
        var query = _db.ProviderAdapterBindings.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(capability))
            query = query.Where(b => b.Capability == capability);
        if (!string.IsNullOrWhiteSpace(environment))
            query = query.Where(b => b.Environment == environment);
        var normalised = ProviderAdapterBinding.NormaliseScope(curriculumScope);
        if (curriculumScope is not null)
            query = normalised is null
                ? query.Where(b => b.CurriculumScope == null)
                : query.Where(b => b.CurriculumScope == normalised);

        return await query.OrderBy(b => b.Capability).ThenBy(b => b.Environment).ThenBy(b => b.CreatedAt)
            .ToListAsync(ct);
    }

    public Task<ProviderAdapterBinding?> ResolveActiveAsync(
        string capability,
        string environment,
        string? curriculumScope,
        CancellationToken ct = default)
    {
        Capabilities.ValidateOrThrow(capability);
        Environments.ValidateOrThrow(environment);
        var normalised = ProviderAdapterBinding.NormaliseScope(curriculumScope);

        return _db.ProviderAdapterBindings
            .AsNoTracking()
            .Where(b => b.Active && b.Capability == capability && b.Environment == environment
                && b.CurriculumScope == normalised)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<ProviderAdapterBinding>> ResolveFallbackChainAsync(
        Guid primaryBindingId,
        CancellationToken ct = default)
    {
        var primary = await _db.ProviderAdapterBindings.AsNoTracking()
            .FirstOrDefaultAsync(b => b.BindingId == primaryBindingId, ct);
        if (primary is null) return Array.Empty<ProviderAdapterBinding>();

        var chain = primary.ReadFallbackChain();
        if (chain.Count == 0) return new[] { primary };

        var resolved = new List<ProviderAdapterBinding> { primary };
        var ids = chain.ToHashSet();
        var entries = await _db.ProviderAdapterBindings.AsNoTracking()
            .Where(b => ids.Contains(b.BindingId))
            .ToListAsync(ct);
        var byId = entries.ToDictionary(b => b.BindingId);

        foreach (var id in chain)
        {
            if (!byId.TryGetValue(id, out var next)) continue;
            if (next.Capability != primary.Capability)
                throw new InvalidOperationException(
                    $"Fallback chain entry {id} targets capability '{next.Capability}', "
                    + $"expected '{primary.Capability}'.");
            resolved.Add(next);
        }

        return resolved;
    }

    public async Task AddAsync(ProviderAdapterBinding binding, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        await ValidateFallbackChainAsync(binding, ct);
        _db.ProviderAdapterBindings.Add(binding);
    }

    public Task ValidateFallbackChainAsync(ProviderAdapterBinding binding, CancellationToken ct = default)
        => ValidateFallbackChainInternalAsync(binding, ct);

    public async Task ActivateAsync(ProviderAdapterBinding target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.PromotionBlockFlag)
            throw new InvalidOperationException("Binding has a promotion-block flag set.");

        var conflict = await _db.ProviderAdapterBindings
            .Where(b => b.Active
                && b.Capability == target.Capability
                && b.Environment == target.Environment
                && b.CurriculumScope == target.CurriculumScope
                && b.BindingId != target.BindingId)
            .ToListAsync(ct);

        foreach (var other in conflict)
        {
            other.Deactivate();
        }

        target.Activate();
    }

    public Task DeactivateAsync(ProviderAdapterBinding target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Deactivate();
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);

    private async Task ValidateFallbackChainInternalAsync(ProviderAdapterBinding binding, CancellationToken ct)
    {
        var chain = binding.ReadFallbackChain();
        if (chain.Count == 0) return;

        var targets = await _db.ProviderAdapterBindings.AsNoTracking()
            .Where(b => chain.Contains(b.BindingId))
            .ToListAsync(ct);

        var missing = chain.Where(id => targets.All(t => t.BindingId != id)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"fallback_chain references unknown binding(s): {string.Join(", ", missing)}.");

        var mismatched = targets.Where(t => t.Capability != binding.Capability).ToList();
        if (mismatched.Count > 0)
            throw new InvalidOperationException(
                $"fallback_chain entries must match capability '{binding.Capability}'. "
                + $"Mismatched: {string.Join(", ", mismatched.Select(m => $"{m.BindingId}:{m.Capability}"))}.");
    }
}
