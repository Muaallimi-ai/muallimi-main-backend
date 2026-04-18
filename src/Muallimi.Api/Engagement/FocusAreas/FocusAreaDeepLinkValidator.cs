using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Muallimi.Api.Engagement.FocusAreas;

/// <summary>
/// T112 (US5) — FocusAreaDeepLinkValidator.
///
/// Every focus-area row MUST point at a live Phase 1 curriculum node the
/// student actually touched (see data-model.md's FocusArea invariant). The
/// calculator calls <see cref="ValidateAsync"/> at write time — rejecting
/// the candidate if the node does not resolve, is unapproved, or has been
/// removed from the catalogue since the signal was captured. Graceful
/// degradation: a rejected deep link yields a neutral "review subject"
/// fallback so the UI can still surface a next step without fabricating a
/// topic.
/// </summary>
public interface IFocusAreaDeepLinkValidator
{
    Task<FocusAreaDeepLinkValidation> ValidateAsync(
        Guid subjectId,
        Guid chapterId,
        Guid topicId,
        CancellationToken ct = default);
}

public sealed record FocusAreaDeepLinkValidation(
    bool IsValid,
    string Phase3Mode,
    string DeepLink,
    string CurriculumNodePath);

public sealed class FocusAreaDeepLinkValidator : IFocusAreaDeepLinkValidator
{
    public const string Phase3ModeStudy = "study";
    public const string Phase3ModeReview = "review";
    private const string StudyLinkPrefix = "/study";

    private readonly IPhase4CurriculumRetrievalClient _retrieval;

    public FocusAreaDeepLinkValidator(IPhase4CurriculumRetrievalClient retrieval)
    {
        _retrieval = retrieval;
    }

    public async Task<FocusAreaDeepLinkValidation> ValidateAsync(
        Guid subjectId,
        Guid chapterId,
        Guid topicId,
        CancellationToken ct = default)
    {
        var resolution = await _retrieval.ResolveNodeAsync(subjectId, chapterId, topicId, ct);

        if (!resolution.Exists || !string.Equals(resolution.Status, "approved", StringComparison.OrdinalIgnoreCase))
        {
            return new FocusAreaDeepLinkValidation(
                IsValid: false,
                Phase3Mode: Phase3ModeReview,
                DeepLink: $"{StudyLinkPrefix}/subject/{subjectId:D}",
                CurriculumNodePath: resolution.Path ?? string.Empty);
        }

        var deepLink = $"{StudyLinkPrefix}/subject/{subjectId:D}/chapter/{chapterId:D}/topic/{topicId:D}";
        return new FocusAreaDeepLinkValidation(
            IsValid: true,
            Phase3Mode: Phase3ModeStudy,
            DeepLink: deepLink,
            CurriculumNodePath: resolution.Path ?? deepLink);
    }
}

public static class FocusAreaDeepLinkValidatorServiceCollectionExtensions
{
    public static IServiceCollection AddPhase4FocusAreaDeepLinkValidator(this IServiceCollection services)
    {
        services.AddScoped<IFocusAreaDeepLinkValidator, FocusAreaDeepLinkValidator>();
        return services;
    }
}
