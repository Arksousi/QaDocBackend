using System.Text.Json;
using System.Text.RegularExpressions;
using QaDocBackend.Models;

namespace QaDocBackend.Infrastructure;

/// <summary>
/// Reads what a model (or a person pasting from one) actually sent and keeps only what is usable.
/// Anything that cannot be understood as a whole is refused outright: a half-parsed reply must
/// never be saved as if it were an answer.
/// </summary>
public static partial class TestCaseParser
{
    /// <summary>A reply that is not JSON at all, or JSON in a shape nothing can be read from.</summary>
    public static GeneratedTestCaseSet Parse(string? json, GenerateOptions options)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new TestCaseGeneratorException("The AI returned nothing. Try again, or use Import / manual.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(StripFences(json), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException ex)
        {
            throw Unreadable($"it is not valid JSON ({ex.Message})");
        }

        using (doc)
        {
            var items = Items(doc.RootElement)
                ?? throw Unreadable("there is no \"testCases\" array in it");

            var result = new GeneratedTestCaseSet();
            var wanted = (options.Categories ?? [])
                .Select(TestCategories.Normalise)
                .Where(c => c != null)
                .Cast<string>()
                .ToArray();

            foreach (var item in items.EnumerateArray())
            {
                if (result.TestCases.Count >= Math.Max(1, options.MaxCases)) break;
                if (Read(item, options, wanted) is { } testCase) result.TestCases.Add(testCase);
            }

            if (result.TestCases.Count == 0)
                throw Unreadable("none of the items in it could be used (every one was missing a title, steps or expected result)");

            return result;
        }
    }

    /// <summary>The array to read from: the object the spec asks for, or a bare array if that is what came back.</summary>
    private static JsonElement? Items(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root;
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, "testCases", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Array)
                return prop.Value;
        }
        return null;
    }

    /// <summary>One item, or null when it is not a test case worth keeping.</summary>
    private static GeneratedTestCase? Read(JsonElement item, GenerateOptions options, string[] wanted)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;

        string? title = Text(item, "title", TestSuiteLimits.MaxTitleLength);
        if (string.IsNullOrWhiteSpace(title)) return null;

        // An unknown category is not guessed at: the item goes, rather than being filed wrongly.
        string? category = TestCategories.Normalise(Text(item, "category", 20));
        if (category == null) return null;
        if (wanted.Length > 0 && !wanted.Contains(category)) return null;

        var steps = Steps(item);
        if (steps.Count == 0) return null;

        string? expected = Text(item, "expected", TestSuiteLimits.MaxFieldLength);
        if (string.IsNullOrWhiteSpace(expected)) return null;

        return new GeneratedTestCase
        {
            Title = title!,
            Category = category,
            // Clamped rather than refused: a model that answers 0 or 9 still wrote a case.
            Priority = Priority(item),
            Preconditions = Text(item, "preconditions", TestSuiteLimits.MaxFieldLength) ?? string.Empty,
            Steps = steps,
            Expected = expected!
        };
    }

    /// <summary>```json … ``` wrappers are common enough to be worth taking off before parsing.</summary>
    public static string StripFences(string json)
    {
        string trimmed = json.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var match = Fences().Match(trimmed);
        return match.Success ? match.Groups[1].Value : trimmed;
    }

    [GeneratedRegex(@"^```[a-zA-Z]*\s*(.*?)\s*```\s*$", RegexOptions.Singleline)]
    private static partial Regex Fences();

    private static TestCaseGeneratorException Unreadable(string why) =>
        new($"The AI reply could not be read as test cases: {why}. Nothing was saved — try again, or use Import / manual.", 400);

    private static string? Text(JsonElement item, string name, int maxLength)
    {
        foreach (var prop in item.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind != JsonValueKind.String) return null;
            string value = prop.Value.GetString() ?? string.Empty;
            return value.Length > maxLength ? value[..maxLength] : value;
        }
        return null;
    }

    private static int Priority(JsonElement item)
    {
        foreach (var prop in item.EnumerateObject())
        {
            if (!string.Equals(prop.Name, "priority", StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int n))
                return Math.Clamp(n, 1, 4);
            if (prop.Value.ValueKind == JsonValueKind.String && int.TryParse(prop.Value.GetString(), out int fromText))
                return Math.Clamp(fromText, 1, 4);
            return 3; // missing or nonsense: the same default a new ticket gets
        }
        return 3;
    }

    /// <summary>Steps trimmed, empties dropped, capped in count and length. An empty result invalidates the item.</summary>
    private static List<string> Steps(JsonElement item)
    {
        var steps = new List<string>();
        foreach (var prop in item.EnumerateObject())
        {
            if (!string.Equals(prop.Name, "steps", StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind != JsonValueKind.Array) return steps;

            foreach (var step in prop.Value.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.String) continue;
                string text = (step.GetString() ?? string.Empty).Trim();
                if (text.Length == 0) continue;
                if (text.Length > TestSuiteLimits.MaxStepLength) text = text[..TestSuiteLimits.MaxStepLength];
                steps.Add(text);
                if (steps.Count >= TestSuiteLimits.MaxSteps) return steps;
            }
            return steps;
        }
        return steps;
    }
}
