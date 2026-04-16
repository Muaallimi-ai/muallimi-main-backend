using Muallimi.Domain.Shared;

namespace Muallimi.Domain.Content;

public class FormatDecision
{
    public Guid DecisionId { get; private set; }
    public Guid LessonId { get; private set; }
    public VisualFormat SelectedFormat { get; private set; }
    public string RuleTriggered { get; private set; } = string.Empty;
    public string? LlmRefinement { get; private set; }
    public string? OverriddenBy { get; private set; }
    public DateTime? OverriddenAt { get; private set; }

    private FormatDecision() { }

    public static FormatDecision Create(
        Guid lessonId, VisualFormat selectedFormat, string ruleTriggered, string? llmRefinement = null)
    {
        return new FormatDecision
        {
            DecisionId = Guid.NewGuid(),
            LessonId = lessonId,
            SelectedFormat = selectedFormat,
            RuleTriggered = ruleTriggered,
            LlmRefinement = llmRefinement
        };
    }

    public void Override(string actorId, VisualFormat newFormat)
    {
        SelectedFormat = newFormat;
        OverriddenBy = actorId;
        OverriddenAt = DateTime.UtcNow;
    }
}
