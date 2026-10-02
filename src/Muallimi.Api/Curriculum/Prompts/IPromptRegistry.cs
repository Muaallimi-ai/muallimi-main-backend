namespace Muallimi.Api.Curriculum.Prompts;

public interface IPromptRegistry
{
    PromptResolution Resolve(string subject, string language, string phase);
}

public sealed record PromptResolution(
    string Body,
    string Key,
    string Version,
    string Sha256);
