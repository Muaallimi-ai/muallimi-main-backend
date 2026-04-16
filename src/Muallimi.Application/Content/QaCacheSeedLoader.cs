using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Muallimi.Application.Content;

/// <summary>
/// T062 - Q&A cache pre-seed loader. Imports the 200 x 4 x 3 pairs from
/// db/Seed/QaCacheSeed/ into QaCacheEntry records with PreSeeded validation status.
/// Target: 2,400 entries (200 per subject x 4 subjects x 3 curriculum types).
/// </summary>
public class QaCacheSeedLoader
{
    private readonly ILogger<QaCacheSeedLoader> _logger;

    public QaCacheSeedLoader(ILogger<QaCacheSeedLoader> logger)
    {
        _logger = logger;
    }

    public List<QaCacheSeedEntry> LoadSeedData(string seedDirectory)
    {
        var entries = new List<QaCacheSeedEntry>();

        if (!Directory.Exists(seedDirectory))
        {
            _logger.LogWarning("Seed directory not found: {Directory}", seedDirectory);
            return entries;
        }

        var files = Directory.GetFiles(seedDirectory, "*.json");
        _logger.LogInformation("Loading Q&A cache seeds from {Count} files in {Directory}", files.Length, seedDirectory);

        foreach (var file in files)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                var parts = fileName.Split('_');
                if (parts.Length < 4)
                {
                    _logger.LogWarning("Skipping seed file with unexpected naming: {File}", fileName);
                    continue;
                }

                // Expected format: {subject}_{curriculumType}_{grade}_{language}.json
                var subject = parts[0];
                var curriculumType = parts[1];
                var grade = parts[2];
                var tutorLanguage = parts[3];

                var json = File.ReadAllText(file);
                var seedPairs = JsonSerializer.Deserialize<List<SeedQaPair>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (seedPairs is null) continue;

                foreach (var pair in seedPairs)
                {
                    entries.Add(new QaCacheSeedEntry(
                        CurriculumType: curriculumType,
                        Subject: subject,
                        Topic: pair.Topic,
                        Grade: grade,
                        TutorLanguage: tutorLanguage,
                        QuestionText: pair.Question,
                        AnswerText: pair.Answer,
                        CreatedBy: "seed-panel"));
                }

                _logger.LogInformation("Loaded {Count} pairs from {File}", seedPairs.Count, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load seed file: {File}", file);
            }
        }

        _logger.LogInformation("Total Q&A cache seed entries loaded: {Count}", entries.Count);
        return entries;
    }
}

public record QaCacheSeedEntry(
    string CurriculumType,
    string Subject,
    string Topic,
    string Grade,
    string TutorLanguage,
    string QuestionText,
    string AnswerText,
    string CreatedBy);

internal record SeedQaPair(string Topic, string Question, string Answer);
