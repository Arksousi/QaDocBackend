using QaDocBackend.Models;

namespace QaDocBackend.Infrastructure;

/// <summary>The providers config may name in TestCaseGenerator:Provider.</summary>
public static class TestCaseProviders
{
    public const string None = "None";
    public const string Groq = "Groq";
    public const string Gemini = "Gemini";
    /// <summary>Canned answers for tests. It never reaches the network, so no test can burn a real key.</summary>
    public const string Mock = "Mock";

    public static readonly string[] All = [None, Groq, Gemini, Mock];

    public static bool IsKnown(string? provider) =>
        All.Contains(provider ?? string.Empty, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Everything the Test Case Generator is allowed to do, read from the "TestCaseGenerator" section
/// (TestCaseGenerator__Provider and friends in environment variables). Defaults live here so an
/// empty configuration still behaves.
/// </summary>
public class TestCaseGeneratorSettings
{
    /// <summary>"None" | "Groq" | "Gemini" | "Mock". "None" keeps the manual import route working.</summary>
    public string Provider { get; set; } = TestCaseProviders.None;

    /// <summary>The Admin's on/off switch for AI generation, so a key cannot be burned by accident.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many times one person may ask for generation in an hour.</summary>
    public int RateLimitPerHour { get; set; } = 5;

    /// <summary>Hard cap on screenshots accepted in one upload.</summary>
    public int MaxImages { get; set; } = 10;

    /// <summary>Hard cap on one screenshot before it is refused.</summary>
    public int MaxImageBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Screenshots wider than this are scaled down before they are sent to the model.</summary>
    public int MaxImageWidth { get; set; } = 1400;

    /// <summary>How many cases one generation may produce at most.</summary>
    public int MaxCases { get; set; } = 20;
}

/// <summary>Groq credentials. Configured via Groq__ApiKey, Groq__VisionModel, Groq__TextModel.</summary>
public class GroqSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string VisionModel { get; set; } = "qwen/qwen3.8-27b";
    public string TextModel { get; set; } = "llama-3.3-70b-versatile";
    /// <summary>How many images Groq accepts in one call; Groq vision limits to at most 3 images per request.</summary>
    public int MaxImages { get; set; } = 3;
}

/// <summary>Gemini credentials.</summary>
public class GeminiSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
}

/// <summary>
/// Turns screenshots plus a business description into test cases. Selected by
/// TestCaseGenerator:Provider, so switching provider is a config change and never a code change.
/// </summary>
public interface ITestCaseGenerator
{
    Task<GeneratedTestCaseSet> GenerateAsync(GenerateRequest req, CancellationToken ct);
}

/// <summary>
/// A provider problem that should be shown as itself rather than as a 500: a missing key, a spent
/// quota, a model that answered with something unparseable. Never carries a key or an image.
/// </summary>
public class TestCaseGeneratorException(string message, int statusCode = 400, Exception? inner = null)
    : Exception(message, inner)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Picks the configured provider. Unknown names fail at startup rather than at the first click.</summary>
public class ConfiguredTestCaseGenerator(
    TestCaseGeneratorSettings settings,
    GroqTestCaseGenerator groq,
    GeminiTestCaseGenerator gemini,
    MockTestCaseGenerator mock) : ITestCaseGenerator
{
    public Task<GeneratedTestCaseSet> GenerateAsync(GenerateRequest req, CancellationToken ct)
    {
        string provider = settings.Provider.Trim();
        if (string.Equals(provider, TestCaseProviders.Groq, StringComparison.OrdinalIgnoreCase))
            return groq.GenerateAsync(req, ct);
        if (string.Equals(provider, TestCaseProviders.Gemini, StringComparison.OrdinalIgnoreCase))
            return gemini.GenerateAsync(req, ct);
        if (string.Equals(provider, TestCaseProviders.Mock, StringComparison.OrdinalIgnoreCase))
            return mock.GenerateAsync(req, ct);

        throw new TestCaseGeneratorException(
            "AI generation is switched off (TestCaseGenerator:Provider = None). Use Import / manual to paste cases in.");
    }
}

/// <summary>
/// A canned answer, used by the test suites and by anybody who wants the flow without a key.
/// It goes through the same parser as a real reply, so the parsing path is covered too.
/// </summary>
public class MockTestCaseGenerator : ITestCaseGenerator
{
    public Task<GeneratedTestCaseSet> GenerateAsync(GenerateRequest req, CancellationToken ct)
    {
        const string canned = """
            {"testCases":[
              {"title":"Sign in with valid credentials opens the projects list","category":"Functional","priority":1,
               "preconditions":"An active account exists","steps":["Open the sign-in page","Enter the username","Enter the password","Submit the form"],
               "expected":"The projects list is shown for the signed-in account."},
              {"title":"Sign in with a wrong password is refused","category":"Negative","priority":2,
               "preconditions":"An active account exists","steps":["Open the sign-in page","Enter a wrong password","Submit the form"],
               "expected":"The form stays on screen and the password box is cleared."},
              {"title":"Password shorter than the minimum is rejected","category":"Boundary","priority":3,
               "preconditions":"None","steps":["Type a 7 character password","Submit the form"],
               "expected":"The form reports that the password must be at least 8 characters."}
            ]}
            """;
        return Task.FromResult(TestCaseParser.Parse(canned, req.Options));
    }
}
