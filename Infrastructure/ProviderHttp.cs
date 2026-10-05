namespace QaDocBackend.Infrastructure;

/// <summary>
/// One HTTP call both providers share: send JSON, get text, and turn every failure into something
/// a person can act on. One retry on a transient failure, because a single blip should not cost a
/// user their attempt; a 429 is never retried — it is answered at once, in words.
/// Nothing here logs a key, a prompt or an image: only status codes.
/// </summary>
public static class ProviderHttp
{
    /// <summary>What a spent quota says, everywhere it can happen.</summary>
    public const string QuotaMessage =
        "AI quota reached, try again in a minute or use manual import.";

    /// <summary>
    /// <paramref name="makeRequest"/> is called once per attempt: an HttpRequestMessage cannot be
    /// sent twice, so the retry builds a fresh one instead of resending the old.
    /// </summary>
    public static async Task<string> PostJsonAsync(
        HttpClient client, Func<HttpRequestMessage> makeRequest, ILogger logger, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await client.SendAsync(makeRequest(), ct);

                if ((int)response.StatusCode == 429)
                    throw new TestCaseGeneratorException(QuotaMessage, 429);

                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = string.Empty;
                    try
                    {
                        errorBody = await response.Content.ReadAsStringAsync(ct);
                    }
                    catch
                    {
                        // ignore error reading body
                    }

                    logger.LogWarning("AI provider error HTTP {StatusCode}: {ErrorBody}", (int)response.StatusCode, errorBody);

                    string? detail = TryExtractErrorMessage(errorBody);
                    string message = !string.IsNullOrWhiteSpace(detail)
                        ? $"The AI service answered HTTP {(int)response.StatusCode}: {detail}"
                        : $"The AI service answered HTTP {(int)response.StatusCode}. Try again shortly, or use Import / manual.";

                    throw new TestCaseGeneratorException(message, 502);
                }

                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException ex)
            {
                if (attempt >= 2 || !CanRetry(ex))
                    throw new TestCaseGeneratorException(
                        "The AI service could not be reached. Check the connection and try again, or use Import / manual.",
                        502, ex);
                logger.LogWarning("AI provider unreachable ({Status}); retrying once.", ex.StatusCode);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // HttpClient.Timeout (90 s) surfaces as a cancellation we did not ask for.
                if (attempt >= 2)
                    throw new TestCaseGeneratorException(
                        "The AI service did not answer in time. Try again, or use Import / manual.", 504, ex);
                logger.LogWarning("AI provider timed out; retrying once.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
        }
    }

    private static bool CanRetry(HttpRequestException ex)
    {
        var status = ex.StatusCode;
        return status is null || (int)status.Value >= 500;
    }

    private static string? TryExtractErrorMessage(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            // OpenAI / Groq / Gemini error object format: { "error": { "message": "..." } }
            if (root.TryGetProperty("error", out var errorProp))
            {
                if (errorProp.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    errorProp.TryGetProperty("message", out var msgProp) &&
                    msgProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return msgProp.GetString();
                }
                if (errorProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return errorProp.GetString();
                }
            }
            if (root.TryGetProperty("message", out var directMsg) &&
                directMsg.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return directMsg.GetString();
            }
        }
        catch
        {
            // Not valid JSON
        }
        return null;
    }
}
