using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Muallimi.Application.Audit;
using Muallimi.Domain.ProviderBindings;
using Muallimi.Infrastructure.ProviderBindings;

namespace Muallimi.Api.ProviderBindings;

/// <summary>
/// T090 (US5) — Admin surface for provider-adapter bindings. Every mutation
/// persists a binding row, emits an <see cref="AuditEventEmitter"/> event with
/// category <c>provider-configuration</c>, and publishes a
/// <see cref="ProviderBindingUpdatedEvent"/> so the ai-service adapter cache
/// invalidates at runtime (provider-adapter-contract invariant: runtime
/// binding changes take effect without a code deploy). Role enforcement
/// (operator / curriculum lead) is applied by the AiOperationsAuthorizationFilter
/// wired in Phase 2 foundational.
/// </summary>
public static class ProviderBindingEndpoints
{
    public const string BaseRoute = "/internal/provider-bindings";

    public static void MapProviderBindingEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(BaseRoute, async (
            IProviderBindingRepository repo,
            string? capability,
            string? environment,
            string? curriculumScope,
            CancellationToken ct) =>
        {
            var items = await repo.ListAsync(capability, environment, curriculumScope, ct);
            return Results.Ok(new { items = items.Select(Map) });
        })
        .WithName("ListProviderBindings")
        .WithTags("ProviderBindings");

        routes.MapGet(BaseRoute + "/{bindingId:guid}", async (
            Guid bindingId,
            IProviderBindingRepository repo,
            CancellationToken ct) =>
        {
            var binding = await repo.GetAsync(bindingId, ct);
            return binding is null
                ? Results.NotFound(new { error = "Binding not found." })
                : Results.Ok(Map(binding));
        })
        .WithName("GetProviderBinding")
        .WithTags("ProviderBindings");

        routes.MapGet(BaseRoute + "/resolve", async (
            IProviderBindingRepository repo,
            string capability,
            string environment,
            string? curriculumScope,
            CancellationToken ct) =>
        {
            try
            {
                var active = await repo.ResolveActiveAsync(capability, environment, curriculumScope, ct);
                if (active is null)
                    return Results.NotFound(new { error = "No active binding for the requested tuple." });

                var chain = await repo.ResolveFallbackChainAsync(active.BindingId, ct);
                return Results.Ok(new
                {
                    primary = Map(active),
                    chain = chain.Select(Map),
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithName("ResolveProviderBinding")
        .WithTags("ProviderBindings");

        routes.MapPost(BaseRoute, async (
            HttpContext http,
            CreateProviderBindingRequest request,
            IProviderBindingRepository repo,
            IProviderBindingUpdatedPublisher publisher,
            AuditEventEmitter audit,
            CancellationToken ct) =>
        {
            if (request is null)
                return Results.BadRequest(new { error = "Request body required." });
            if (string.IsNullOrWhiteSpace(request.ChangeActor))
                return Results.BadRequest(new { error = "change_actor is required." });
            if (string.IsNullOrWhiteSpace(request.ChangeReason))
                return Results.BadRequest(new { error = "change_reason is required." });

            ProviderAdapterBinding binding;
            try
            {
                binding = ProviderAdapterBinding.Create(
                    capability: request.Capability,
                    environment: request.Environment,
                    curriculumScope: request.CurriculumScope,
                    providerIdentifier: request.ProviderIdentifier,
                    fallbackChain: request.FallbackChain,
                    providerConfigurationRef: request.ProviderConfigurationRef);

                await repo.AddAsync(binding, ct);
                await repo.SaveChangesAsync(ct);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            var (correlationId, tenantId) = ResolveContext(http);
            audit.Emit(BuildAuditEvent(binding, "binding-created", request.ChangeActor, tenantId, correlationId, request.ChangeReason));
            await publisher.PublishAsync(BuildEvent(
                binding, ProviderBindingEventTypes.Created, request.ChangeActor, correlationId), ct);

            return Results.Created($"{BaseRoute}/{binding.BindingId}", Map(binding));
        })
        .WithName("CreateProviderBinding")
        .WithTags("ProviderBindings");

        routes.MapPost(BaseRoute + "/{bindingId:guid}/activate", async (
            Guid bindingId,
            HttpContext http,
            ChangeReasonRequest request,
            IProviderBindingRepository repo,
            IProviderBindingUpdatedPublisher publisher,
            AuditEventEmitter audit,
            CancellationToken ct) =>
        {
            if (request is null) return Results.BadRequest(new { error = "Request body required." });
            if (string.IsNullOrWhiteSpace(request.ChangeActor))
                return Results.BadRequest(new { error = "change_actor is required." });
            if (string.IsNullOrWhiteSpace(request.ChangeReason))
                return Results.BadRequest(new { error = "change_reason is required." });

            var binding = await repo.GetAsync(bindingId, ct);
            if (binding is null) return Results.NotFound(new { error = "Binding not found." });

            try
            {
                await repo.ActivateAsync(binding, ct);
                await repo.SaveChangesAsync(ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }

            var (correlationId, tenantId) = ResolveContext(http);
            audit.Emit(BuildAuditEvent(binding, "binding-activated", request.ChangeActor, tenantId, correlationId, request.ChangeReason));
            await publisher.PublishAsync(BuildEvent(
                binding, ProviderBindingEventTypes.Activated, request.ChangeActor, correlationId), ct);

            return Results.Ok(Map(binding));
        })
        .WithName("ActivateProviderBinding")
        .WithTags("ProviderBindings");

        routes.MapPost(BaseRoute + "/{bindingId:guid}/deactivate", async (
            Guid bindingId,
            HttpContext http,
            ChangeReasonRequest request,
            IProviderBindingRepository repo,
            IProviderBindingUpdatedPublisher publisher,
            AuditEventEmitter audit,
            CancellationToken ct) =>
        {
            if (request is null) return Results.BadRequest(new { error = "Request body required." });
            if (string.IsNullOrWhiteSpace(request.ChangeActor))
                return Results.BadRequest(new { error = "change_actor is required." });
            if (string.IsNullOrWhiteSpace(request.ChangeReason))
                return Results.BadRequest(new { error = "change_reason is required." });

            var binding = await repo.GetAsync(bindingId, ct);
            if (binding is null) return Results.NotFound(new { error = "Binding not found." });

            await repo.DeactivateAsync(binding, ct);
            await repo.SaveChangesAsync(ct);

            var (correlationId, tenantId) = ResolveContext(http);
            audit.Emit(BuildAuditEvent(binding, "binding-deactivated", request.ChangeActor, tenantId, correlationId, request.ChangeReason));
            await publisher.PublishAsync(BuildEvent(
                binding, ProviderBindingEventTypes.Deactivated, request.ChangeActor, correlationId), ct);

            return Results.Ok(Map(binding));
        })
        .WithName("DeactivateProviderBinding")
        .WithTags("ProviderBindings");

        routes.MapPut(BaseRoute + "/{bindingId:guid}/fallback-chain", async (
            Guid bindingId,
            HttpContext http,
            UpdateFallbackChainRequest request,
            IProviderBindingRepository repo,
            IProviderBindingUpdatedPublisher publisher,
            AuditEventEmitter audit,
            CancellationToken ct) =>
        {
            if (request is null) return Results.BadRequest(new { error = "Request body required." });
            if (string.IsNullOrWhiteSpace(request.ChangeActor))
                return Results.BadRequest(new { error = "change_actor is required." });
            if (string.IsNullOrWhiteSpace(request.ChangeReason))
                return Results.BadRequest(new { error = "change_reason is required." });

            var binding = await repo.GetAsync(bindingId, ct);
            if (binding is null) return Results.NotFound(new { error = "Binding not found." });

            try
            {
                binding.UpdateFallbackChain(request.FallbackChain ?? Array.Empty<Guid>());
                await repo.ValidateFallbackChainAsync(binding, ct);
                await repo.SaveChangesAsync(ct);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            var (correlationId, tenantId) = ResolveContext(http);
            audit.Emit(BuildAuditEvent(binding, "fallback-chain-updated", request.ChangeActor, tenantId, correlationId, request.ChangeReason));
            await publisher.PublishAsync(BuildEvent(
                binding, ProviderBindingEventTypes.FallbackUpdated, request.ChangeActor, correlationId), ct);

            return Results.Ok(Map(binding));
        })
        .WithName("UpdateProviderBindingFallbackChain")
        .WithTags("ProviderBindings");
    }

    public static void AddProviderBindingServices(this IServiceCollection services)
    {
        services.AddScoped<IProviderBindingRepository, ProviderBindingRepository>();
        services.AddSingleton<InMemoryProviderBindingUpdatedPublisher>();
        services.AddSingleton<IProviderBindingUpdatedPublisher>(
            sp => sp.GetRequiredService<InMemoryProviderBindingUpdatedPublisher>());
    }

    private static object Map(ProviderAdapterBinding b) => new
    {
        binding_id = b.BindingId,
        capability = b.Capability,
        environment = b.Environment,
        curriculum_scope = b.CurriculumScope,
        provider_identifier = b.ProviderIdentifier,
        provider_configuration_ref = b.ProviderConfigurationRef,
        fallback_chain = b.ReadFallbackChain(),
        active = b.Active,
        promotion_block_flag = b.PromotionBlockFlag,
        created_at = b.CreatedAt,
        updated_at = b.UpdatedAt,
    };

    private static AuditEvent BuildAuditEvent(
        ProviderAdapterBinding binding,
        string action,
        string actor,
        string tenantId,
        string correlationId,
        string reason)
        => new()
        {
            EventCategory = "provider-configuration",
            Action = action,
            TargetType = nameof(ProviderAdapterBinding),
            TargetId = binding.BindingId.ToString(),
            ActorId = actor,
            TenantId = tenantId,
            Outcome = "succeeded",
            CorrelationId = correlationId,
            Reason = $"capability={binding.Capability}; env={binding.Environment}; scope={binding.CurriculumScope ?? "global"}; {reason}",
        };

    private static ProviderBindingUpdatedEvent BuildEvent(
        ProviderAdapterBinding binding,
        string eventType,
        string actor,
        string correlationId)
        => new(
            EventId: Guid.NewGuid().ToString("N"),
            EventType: eventType,
            BindingId: binding.BindingId,
            Capability: binding.Capability,
            Environment: binding.Environment,
            CurriculumScope: binding.CurriculumScope,
            ProviderIdentifier: binding.ProviderIdentifier,
            Active: binding.Active,
            ActorId: actor,
            CorrelationId: correlationId,
            OccurredAt: DateTime.UtcNow);

    private static (string CorrelationId, string TenantId) ResolveContext(HttpContext http)
    {
        var correlationId = http.Items["CorrelationId"]?.ToString()
            ?? http.Request.Headers["X-Correlation-Id"].FirstOrDefault()
            ?? Guid.NewGuid().ToString("N");
        var tenantId = http.Items["TenantId"]?.ToString()
            ?? http.Request.Headers["X-Tenant-Id"].FirstOrDefault()
            ?? "local";
        return (correlationId, tenantId);
    }
}

public record CreateProviderBindingRequest(
    string Capability,
    string Environment,
    string? CurriculumScope,
    string ProviderIdentifier,
    string? ProviderConfigurationRef,
    IReadOnlyList<Guid>? FallbackChain,
    string ChangeActor,
    string ChangeReason);

public record ChangeReasonRequest(string ChangeActor, string ChangeReason);

public record UpdateFallbackChainRequest(
    IReadOnlyList<Guid>? FallbackChain,
    string ChangeActor,
    string ChangeReason);
