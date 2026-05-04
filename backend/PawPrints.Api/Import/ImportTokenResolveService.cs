using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PawPrints.Api.Contracts;

namespace PawPrints.Api.Import;

public sealed class ImportTokenResolveService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<ImportTokenResolveService> logger
)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<ImportResolveTokensResponse> ResolveAsync(
        ImportResolveTokensRequest request,
        CancellationToken cancellationToken
    )
    {
        var tokens = request.Tokens.Where(token => !string.IsNullOrWhiteSpace(token)).Distinct().ToArray();

        if (tokens.Length == 0)
        {
            return new ImportResolveTokensResponse(AiAvailable: false, Matches: []);
        }

        var apiKey = ProgramConfiguration.GetFirstConfiguredValue(
            configuration,
            "Import:OpenAiApiKey",
            "OPENAI_API_KEY"
        );

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogInformation(
                "Import token AI resolve skipped because no OpenAI API key is configured import token count {TokenCount}",
                tokens.Length
            );
            return new ImportResolveTokensResponse(AiAvailable: false, Matches: []);
        }

        var model = configuration["Import:OpenAiModel"] ?? "gpt-4o-mini";

        var knownTypesJson = request.KnownTypes is { Count: > 0 }
            ? JsonSerializer.Serialize(request.KnownTypes, SerializerOptions)
            : "[]";

        var prompt =
            $"""
            You map short puppy-care activity phrases to app event types.

            Known types (prefer these ids when they fit): {knownTypesJson}

            For each input token, respond with JSON ONLY: a single object with key "matches" whose value is an array of objects.
            Each object must include: token (string), typeId (string), isNew (boolean), label (string or null), emoji (string or null).

            Rules:
            - typeId is lowercase slug: letters, digits, hyphen only; max 40 chars.
            - If the token matches a known type id or meaning (wee/pee, poo/poop, food/eat, sleep/nap, wake, etc.), use that canonical typeId and isNew=false.
            - If it is a valid activity not in the list (e.g. chew, chill), set isNew=true, pick typeId, and give a short Title Case label and one emoji.
            - Use British "wee" = pee, "poo" = poop.
            - Include every token from the input list exactly once in matches[].token (same spelling as input).

            Tokens:
            {string.Join("\n", tokens.Select((token, index) => $"{index + 1}. {token}"))}
            """;

        string rawJson;
        try
        {
            rawJson = await CallOpenAiChatAsync(apiKey, model, prompt, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "OpenAI request failed during import token resolve");
            return new ImportResolveTokensResponse(AiAvailable: false, Matches: []);
        }

        var normalizedJson = NormalizeAssistantJson(rawJson);

        AiMatchesEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<AiMatchesEnvelope>(normalizedJson, DeserializeOptions);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Could not parse OpenAI JSON for import resolve raw length {Length}",
                rawJson.Length
            );
            return new ImportResolveTokensResponse(AiAvailable: false, Matches: []);
        }

        if (envelope?.Matches is null || envelope.Matches.Count == 0)
        {
            logger.LogInformation("Import token AI resolve returned no matches");
            return new ImportResolveTokensResponse(AiAvailable: true, Matches: []);
        }

        var matches = envelope.Matches
            .Select(match => new ImportTokenMatchDto(
                Token: match.Token ?? "",
                TypeId: SanitizeTypeId(match.TypeId ?? ""),
                IsNew: match.IsNew,
                Label: string.IsNullOrWhiteSpace(match.Label) ? null : match.Label.Trim(),
                Emoji: string.IsNullOrWhiteSpace(match.Emoji) ? null : match.Emoji.Trim()
            ))
            .Where(match => !string.IsNullOrWhiteSpace(match.Token) && !string.IsNullOrWhiteSpace(match.TypeId))
            .ToArray();

        logger.LogInformation(
            "Import token AI resolve completed model {Model} match count {MatchCount}",
            model,
            matches.Length
        );

        return new ImportResolveTokensResponse(AiAvailable: true, Matches: matches);
    }

    private async Task<string> CallOpenAiChatAsync(
        string apiKey,
        string model,
        string userPrompt,
        CancellationToken cancellationToken
    )
    {
        var client = httpClientFactory.CreateClient(nameof(ImportTokenResolveService));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.openai.com/v1/chat/completions"
        );
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var body = new OpenAiChatRequest(
            Model: model,
            Messages: [new OpenAiMessage("user", userPrompt)],
            ResponseFormat: new OpenAiResponseFormat("json_object"),
            Temperature: 0.1
        );

        request.Content = new StringContent(
            JsonSerializer.Serialize(body, SerializerOptions),
            Encoding.UTF8,
            "application/json"
        );

        var response = await client.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var slice = responseText.Length > 500 ? responseText[..500] : responseText;
            throw new InvalidOperationException($"OpenAI HTTP {(int)response.StatusCode}: {slice}");
        }

        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;
        var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("OpenAI returned empty message content");
        }

        return content;
    }

    private static string NormalizeAssistantJson(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = trimmed.IndexOf('\n');
            if (firstBreak > 0)
            {
                trimmed = trimmed[(firstBreak + 1)..];
            }

            var endFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (endFence >= 0)
            {
                trimmed = trimmed[..endFence];
            }
        }

        return trimmed.Trim();
    }

    private static string SanitizeTypeId(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        foreach (var character in raw.ToLowerInvariant().Trim())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            {
                builder.Append(character);
            }
        }

        var slug = builder.ToString();

        return slug.Length > 40 ? slug[..40] : slug;
    }

    private sealed record OpenAiChatRequest(
        string Model,
        IReadOnlyCollection<OpenAiMessage> Messages,
        OpenAiResponseFormat ResponseFormat,
        double Temperature
    );

    private sealed record OpenAiMessage(string Role, string Content);

    private sealed record OpenAiResponseFormat(string Type);

    private sealed class AiMatchesEnvelope
    {
        public List<AiMatchItem>? Matches { get; set; }
    }

    private sealed class AiMatchItem
    {
        public string? Token { get; set; }
        public string? TypeId { get; set; }
        public bool IsNew { get; set; }
        public string? Label { get; set; }
        public string? Emoji { get; set; }
    }
}
