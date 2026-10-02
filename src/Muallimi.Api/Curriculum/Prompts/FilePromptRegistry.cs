using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Muallimi.Api.Curriculum.Prompts;

public sealed class FilePromptRegistry : IPromptRegistry
{
    private readonly string _rootPath;
    private readonly ILogger<FilePromptRegistry> _logger;
    private readonly ConcurrentDictionary<string, PromptResolution> _cache = new();

    public FilePromptRegistry(string rootPath, ILogger<FilePromptRegistry> logger)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Prompt registry root path is required.", nameof(rootPath));

        _rootPath = rootPath;
        _logger = logger;

        if (!Directory.Exists(_rootPath))
        {
            throw new DirectoryNotFoundException(
                $"Prompt registry root path does not exist: {_rootPath}. " +
                "Check that the prompts/ folder is mounted or COPY'd into the container, " +
                "or set PromptRegistry:RootPath to the correct location.");
        }

        _logger.LogInformation("FilePromptRegistry initialized. Root path: {RootPath}", _rootPath);
    }

    public PromptResolution Resolve(string subject, string language, string phase)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new ArgumentException("subject is required.", nameof(subject));
        if (string.IsNullOrWhiteSpace(language)) throw new ArgumentException("language is required.", nameof(language));
        if (string.IsNullOrWhiteSpace(phase)) throw new ArgumentException("phase is required.", nameof(phase));

        var folderPath = Path.Combine(_rootPath, subject, language);
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException(
                $"No prompt folder for (subject={subject}, language={language}). " +
                $"Expected: {folderPath}.");
        }

        var activeJsonPath = Path.Combine(folderPath, "active.json");
        if (!File.Exists(activeJsonPath))
        {
            throw new FileNotFoundException(
                $"Missing active.json for (subject={subject}, language={language}). Expected: {activeJsonPath}.",
                activeJsonPath);
        }

        var activeJson = File.ReadAllText(activeJsonPath);
        Dictionary<string, string>? activeMap;
        try
        {
            activeMap = JsonSerializer.Deserialize<Dictionary<string, string>>(activeJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"active.json at {activeJsonPath} is not valid JSON: {ex.Message}", ex);
        }

        if (activeMap is null || !activeMap.TryGetValue(phase, out var version) || string.IsNullOrWhiteSpace(version))
        {
            throw new KeyNotFoundException(
                $"active.json at {activeJsonPath} has no entry for phase '{phase}'. " +
                $"Known phases: [{string.Join(", ", activeMap?.Keys ?? Enumerable.Empty<string>())}].");
        }

        var compositeKey = $"{subject}/{language}/{phase}";
        var cacheKey = $"{compositeKey}@{version}";

        return _cache.GetOrAdd(cacheKey, _ =>
        {
            var filePath = Path.Combine(folderPath, $"{phase}.{version}.md");
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    $"active.json points at version '{version}' for phase '{phase}' but the file is missing. Expected: {filePath}.",
                    filePath);
            }

            var raw = File.ReadAllText(filePath);
            var body = StripFrontmatter(raw);
            if (string.IsNullOrWhiteSpace(body))
                throw new InvalidOperationException($"Prompt file {filePath} has no body after stripping frontmatter.");

            var sha = ComputeSha256Hex(body);
            _logger.LogInformation(
                "Loaded prompt {Key}@{Version} from {Path} (body {Bytes} bytes, sha256 {Sha}...)",
                compositeKey, version, filePath, body.Length, sha[..12]);

            return new PromptResolution(body, compositeKey, version, sha);
        });
    }

    private static string StripFrontmatter(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var trimmedStart = raw.TrimStart('﻿');
        if (!trimmedStart.StartsWith("---")) return trimmedStart;

        var afterOpening = trimmedStart[3..].TrimStart(' ', '\t');
        if (afterOpening.Length == 0 || (afterOpening[0] != '\n' && afterOpening[0] != '\r'))
            return trimmedStart;

        var searchFrom = 0;
        var body = afterOpening;
        while (searchFrom < body.Length)
        {
            var idx = body.IndexOf("---", searchFrom, StringComparison.Ordinal);
            if (idx < 0) break;

            var atLineStart = idx == 0 || body[idx - 1] == '\n';
            var endOk = idx + 3 == body.Length || body[idx + 3] == '\n' || body[idx + 3] == '\r';

            if (atLineStart && endOk)
            {
                var after = idx + 3;
                if (after < body.Length && body[after] == '\r') after++;
                if (after < body.Length && body[after] == '\n') after++;
                return body[after..];
            }
            searchFrom = idx + 3;
        }
        return trimmedStart;
    }

    private static string ComputeSha256Hex(string body)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexStringLower(bytes);
    }
}
