using System.Text;
using System.Text.Json;
using QaDocBackend.Models;

namespace QaDocBackend.Infrastructure;

public static class QcPrompts
{
    // ---------- Screen Analysis Prompt (Vision AI) ----------

    public static string ScreenSystem(string language)
    {
        string langNotice = string.Equals(language, "ar", StringComparison.OrdinalIgnoreCase)
            ? "Respond in Arabic (العربية)."
            : "Respond in English.";

        return $$"""
            You are a senior technical writer and software analyst.
            Analyze the provided application screenshot and describe its user interface, elements, and possible user actions.

            {{langNotice}}

            Return JSON ONLY with this exact structure (no markdown fences, no extra text):
            {
              "screenTitle": "Clear, concise title for this screen",
              "purpose": "Primary purpose of this screen in one or two sentences",
              "visibleElements": [
                {
                  "name": "Element label or heading",
                  "type": "button | input | table | card | dropdown | link | tab | modal",
                  "description": "What this element represents or contains"
                }
              ],
              "userActions": [
                "Action a user can take here, e.g. click Save, filter by date, enter username"
              ],
              "navigatesTo": "Where the user goes after primary action, if evident",
              "notes": "Any validation hints, badge counts, empty states, or alerts visible"
            }

            Guidelines:
            - Report ONLY what is clearly visible on screen. Do not invent or assume unseen features.
            - If an element has an obvious validation rule or status (e.g. required asterisk, disabled button, error message), note it.
            """.Trim();
    }

    public static string ScreenUser(string? caption, string? businessDescription, string? runningSummary)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(businessDescription))
        {
            sb.AppendLine("Application Business Context:");
            sb.AppendLine(businessDescription.Trim());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(caption))
        {
            sb.AppendLine($"This screen is titled/captioned: \"{caption.Trim()}\"");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(runningSummary))
        {
            sb.AppendLine("Context of preceding screens in this workflow sequence:");
            sb.AppendLine(runningSummary.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("Analyze the attached screenshot and return the JSON analysis.");
        return sb.ToString().TrimEnd();
    }

    // ---------- Text-only Document Generation Prompt ----------

    public static string DocumentSystem(string kind, string appName, string language)
    {
        bool isManual = string.Equals(kind, QcDocumentKinds.UserManual, StringComparison.OrdinalIgnoreCase);
        string langNotice = string.Equals(language, "ar", StringComparison.OrdinalIgnoreCase)
            ? "Write the entire document in professional Arabic (العربية)."
            : "Write the entire document in professional English.";

        if (isManual)
        {
            return $"""
                You are a senior technical writer writing a task-oriented User Manual for "{appName}".
                {langNotice}

                You will receive the sequenced screen analyses of the application.
                Produce a comprehensive, user-friendly User Manual in clean Markdown.

                Follow this exact heading structure:
                # {appName} — User Manual

                ## 1. Introduction
                (Purpose of the system, who it is for, key concepts)

                ## 2. Getting Started
                (How to access the application, signing in, main navigation layout)

                ## 3. How-to Guides
                (Write practical step-by-step instructions for each major user task derived from the screen sequence.
                 Format steps as numbered lists: 1. ... 2. ...
                 Reference the screen names and buttons explicitly, e.g. 'On the **Projects** screen, click **New Project**'.)

                ## 4. Tips & Best Practices
                (Helpful advice, shortcuts, and recommendations based on the features seen)

                ## 5. Troubleshooting & FAQ
                (Common error messages, validations observed on screens, and how to resolve them)

                ## 6. Glossary
                (Definitions of application terms, roles, and status values seen on screens)

                Rules:
                - Do NOT invent features that are not visible or mentioned in the screen summaries.
                - Keep headings exact and stable.
                - Use clear, active voice suitable for end users.
                """.Trim();
        }

        // Product Documentation
        return $"""
            You are a senior software product analyst and technical architect writing comprehensive Product Documentation for "{appName}".
            {langNotice}

            You will receive the sequenced screen analyses of the application and the business description.
            Produce complete, formal Product Documentation in clean Markdown.

            Follow this exact heading structure:
            # {appName} — Product Specification & Documentation

            ## 1. Document Overview
            - **Application:** {appName}
            - **Document Version:** Draft / Current
            - **Date:** {DateTime.UtcNow:yyyy-MM-dd}

            ## 2. Executive Overview
            (High-level summary of the system and business value)

            ## 3. Target Users & Personas
            (Who uses this software, their primary objectives)

            ## 4. Core Features
            (Bullet list and breakdown of capabilities demonstrated across screens)

            ## 5. Screen Specifications
            (For EACH screen in order:
             ### Screen [N]: [Caption/Title]
             - **Purpose:** ...
             - **UI Elements:** ...
             - **User Actions:** ...
             - **Outcomes & Navigation:** ...
            )

            ## 6. Main User Flows
            (End-to-end journey reconstructed from the sequenced screens)

            ## 7. Validation Rules & System States
            (Field limits, required markers, error notifications, disabled states observed)

            ## 8. Roles & Permissions
            (Roles, permission levels, or guest constraints visible or stated)

            ## 9. Glossary
            (Terminology, domain terms, identifiers used in the app)

            ## 10. Open Questions
            (Unresolved details, unconfirmed backend logic, or edge cases that cannot be confirmed from screenshots alone)

            Rules:
            - Base all documentation strictly on the provided screen analyses and business description. Never hallucinate non-existent modules.
            - If something is uncertain, document what IS known under the screen section and place the uncertainty under ## 10. Open Questions.
            - Keep headings exact and predictable.
            """.Trim();
    }

    public static string DocumentUser(
        string appName, string? businessDescription,
        List<(int sortOrder, string? caption, QcScreenSummary summary)> screens)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Application: {appName}");
        if (!string.IsNullOrWhiteSpace(businessDescription))
        {
            sb.AppendLine("Business Description:");
            sb.AppendLine(businessDescription.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("Sequenced Screen Analyses:");
        for (int i = 0; i < screens.Count; i++)
        {
            var (order, caption, summary) = screens[i];
            sb.AppendLine($"--- Screen {i + 1} (Step #{order}): \"{caption ?? summary.ScreenTitle}\" ---");
            sb.AppendLine($"Title: {summary.ScreenTitle}");
            sb.AppendLine($"Purpose: {summary.Purpose}");
            if (summary.VisibleElements.Count > 0)
            {
                sb.AppendLine("Visible Elements:");
                foreach (var el in summary.VisibleElements)
                {
                    sb.AppendLine($"  - [{el.Type}] {el.Name}: {el.Description}");
                }
            }
            if (summary.UserActions.Count > 0)
            {
                sb.AppendLine("User Actions:");
                foreach (var action in summary.UserActions) sb.AppendLine($"  - {action}");
            }
            if (!string.IsNullOrWhiteSpace(summary.NavigatesTo))
                sb.AppendLine($"Navigates to: {summary.NavigatesTo}");
            if (!string.IsNullOrWhiteSpace(summary.Notes))
                sb.AppendLine($"Notes / Validation: {summary.Notes}");
            sb.AppendLine();
        }

        sb.AppendLine("Now generate the complete Markdown document according to the system instructions.");
        return sb.ToString();
    }

    // ---------- Ready-made prompt for Manual route ----------

    public static string PastePrompt(
        string kind, string appName, string? businessDescription, string language,
        List<(int sortOrder, string? caption, string? summaryJson)> screens)
    {
        var parsedScreens = new List<(int, string?, QcScreenSummary)>();
        foreach (var (order, caption, json) in screens)
        {
            QcScreenSummary summary;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    summary = JsonSerializer.Deserialize<QcScreenSummary>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? new QcScreenSummary { ScreenTitle = caption ?? $"Screen #{order}" };
                }
                catch
                {
                    summary = new QcScreenSummary { ScreenTitle = caption ?? $"Screen #{order}" };
                }
            }
            else
            {
                summary = new QcScreenSummary { ScreenTitle = caption ?? $"Screen #{order}" };
            }
            parsedScreens.Add((order, caption, summary));
        }

        return $"SYSTEM\n{DocumentSystem(kind, appName, language)}\n\nUSER\n{DocumentUser(appName, businessDescription, parsedScreens)}";
    }
}
