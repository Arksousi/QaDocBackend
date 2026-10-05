using System.Text;
using QaDocBackend.Models;

namespace QaDocBackend.Infrastructure;

/// <summary>
/// The one prompt both providers send, and the same text the manual route hands to the user:
/// GET testsuites/{id}/prompt returns what a person can paste into any chat AI, so a missing key
/// or a spent quota never blocks the work.
/// </summary>
public static class TestCasePrompt
{
    /// <summary>The exact reply shape, quoted to the model so it cannot pick its own.</summary>
    public const string RequiredShape =
        "{\"testCases\":[{\"title\":\"\",\"category\":\"Functional|Negative|Boundary|UI\",\"priority\":1," +
        "\"preconditions\":\"\",\"steps\":[\"\",\"\"],\"expected\":\"\"}]}";

    /// <summary>The rules both providers are given. Never mentions a feature that is not in the input.</summary>
    public static string System(GenerateOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a senior QA engineer writing test cases for another engineer to review, approve and run.");
        sb.AppendLine("Base every test case ONLY on what is visible in the screenshots and on the business description below.");
        sb.AppendLine("Never invent features, screens, fields, buttons or behaviour that are not there.");
        sb.AppendLine("Cover functional, negative, boundary and UI cases.");
        sb.AppendLine("Each step must be something a person can actually do, and each case must say what should happen.");
        sb.AppendLine();
        sb.AppendLine("Return JSON ONLY, with no commentary, no markdown and no code fences, in exactly this shape:");
        sb.AppendLine(RequiredShape);
        sb.AppendLine();
        sb.AppendLine("Rules for every item:");
        sb.AppendLine("- title: 1-200 characters, say what is being checked.");
        sb.AppendLine("- category: one of Functional, Negative, Boundary, UI.");
        sb.AppendLine("- priority: an integer from 1 (most important) to 4 (least).");
        sb.AppendLine("- preconditions: what must already be true, or an empty string.");
        sb.AppendLine("- steps: an array of 1 to 20 short strings, each at most 500 characters.");
        sb.AppendLine("- expected: what should happen, at least one sentence.");

        if (options.MaxCases > 0)
            sb.AppendLine($"- Return at most {options.MaxCases} test cases.");

        var categories = Normalised(options.Categories);
        if (categories.Length > 0)
            sb.AppendLine($"- Only these categories: {string.Join(", ", categories)}.");

        if (!string.IsNullOrWhiteSpace(options.Language))
            sb.AppendLine($"- Write everything in {options.Language.Trim()}.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>What the user half of the call says: the description, then what the images are for.</summary>
    public static string User(string businessDescription, int imageCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Business description:");
        sb.AppendLine(string.IsNullOrWhiteSpace(businessDescription)
            ? "(none given — rely on the screenshots)"
            : businessDescription.Trim());
        sb.AppendLine();
        sb.AppendLine(imageCount == 1
            ? "The attached screenshot shows the application."
            : $"The {imageCount} attached screenshots show the application.");
        sb.AppendLine("Read them before writing any test case.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>The whole thing as one block of text, ready to paste into any chat AI.</summary>
    public static string PastePrompt(string businessDescription, int imageCount, GenerateOptions options) =>
        $"SYSTEM\n{System(options)}\n\nUSER\n{User(businessDescription, imageCount)}";

    /// <summary>Blank entries dropped and case folded, so a padded request cannot smuggle a value in.</summary>
    private static string[] Normalised(string[]? values) =>
        (values ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Select(v => TestCategories.Normalise(v) ?? v)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
