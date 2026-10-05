using System.Text;
using QaDocBackend.Models;

namespace QaDocBackend.Infrastructure;

public class MockDocumentGenerator : IDocumentGenerator
{
    public Task<QcScreenSummary> AnalyzeScreenAsync(
        GenerateImage image, string? caption, string? businessDescription,
        string? runningSummary, string language, CancellationToken ct)
    {
        string title = string.IsNullOrWhiteSpace(caption) ? "Application Dashboard" : caption.Trim();
        var summary = new QcScreenSummary
        {
            ScreenTitle = title,
            Purpose = $"Displays key metrics, navigation links, and operational controls for {title}.",
            VisibleElements = new List<QcVisibleElement>
            {
                new() { Name = "Main Navigation", Type = "tab", Description = "Allows switching between application modules" },
                new() { Name = "Action Button", Type = "button", Description = "Submits the primary workflow action" },
                new() { Name = "Filter Bar", Type = "input", Description = "Searches and filters displayed records" }
            },
            UserActions = new List<string>
            {
                "Navigate between sections using the top bar",
                "Apply filters to narrow down the table data",
                "Click the primary action button to trigger creation"
            },
            NavigatesTo = "Details view or confirmation state upon submission",
            Notes = "Field validation requires non-empty inputs. Active items are highlighted."
        };

        return Task.FromResult(summary);
    }

    public Task<string> GenerateDocumentMarkdownAsync(
        string kind, string appName, string? businessDescription,
        List<(int sortOrder, string? caption, QcScreenSummary summary)> screens,
        string language, CancellationToken ct)
    {
        bool isManual = string.Equals(kind, QcDocumentKinds.UserManual, StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();

        if (isManual)
        {
            sb.AppendLine($"# {appName} — User Manual");
            sb.AppendLine();
            sb.AppendLine("## 1. Introduction");
            sb.AppendLine($"Welcome to {appName}. This guide helps you navigate and operate the application effectively.");
            sb.AppendLine();
            sb.AppendLine("## 2. Getting Started");
            sb.AppendLine("To access the system, sign in with your assigned credentials or guest access.");
            sb.AppendLine("The top bar provides instant access to all core modules and personal profile settings.");
            sb.AppendLine();
            sb.AppendLine("## 3. How-to Guides");
            for (int i = 0; i < screens.Count; i++)
            {
                var (order, caption, summary) = screens[i];
                string name = caption ?? summary.ScreenTitle;
                sb.AppendLine($"### How to use {name}");
                sb.AppendLine($"1. Open the **{name}** screen from the navigation menu.");
                sb.AppendLine($"2. Review the displayed items: {summary.Purpose}");
                sb.AppendLine("3. Perform the desired action or fill in required fields.");
                sb.AppendLine("4. Click the confirmation button to complete the operation.");
                sb.AppendLine();
            }
            sb.AppendLine("## 4. Tips & Best Practices");
            sb.AppendLine("- Keep filters active to focus on your immediate workload.");
            sb.AppendLine("- Review all required fields before submitting to prevent validation prompts.");
            sb.AppendLine();
            sb.AppendLine("## 5. Troubleshooting & FAQ");
            sb.AppendLine("- **Q: Why is the submit button disabled?**");
            sb.AppendLine("  A: Ensure all mandatory inputs are filled and meet the length constraints.");
            sb.AppendLine("- **Q: What happens if my session expires?**");
            sb.AppendLine("  A: Sign in again; any unsaved draft content will need to be re-entered.");
            sb.AppendLine();
            sb.AppendLine("## 6. Glossary");
            sb.AppendLine($"- **{appName}**: The primary application workspace.");
            sb.AppendLine("- **Draft**: A record currently being edited and not yet published.");
            sb.AppendLine("- **Approved**: A finalized record verified by the team.");
        }
        else
        {
            sb.AppendLine($"# {appName} — Product Specification & Documentation");
            sb.AppendLine();
            sb.AppendLine("## 1. Document Overview");
            sb.AppendLine($"- **Application:** {appName}");
            sb.AppendLine("- **Document Version:** 1.0 (Automated Analysis)");
            sb.AppendLine($"- **Date:** {DateTime.UtcNow:yyyy-MM-dd}");
            sb.AppendLine();
            sb.AppendLine("## 2. Executive Overview");
            sb.AppendLine(string.IsNullOrWhiteSpace(businessDescription)
                ? $"{appName} is an enterprise-grade platform designed for structured quality assurance and tracking."
                : businessDescription.Trim());
            sb.AppendLine();
            sb.AppendLine("## 3. Target Users & Personas");
            sb.AppendLine("- **QA Engineers & Testers:** Verify workflows, log bugs, and inspect screens.");
            sb.AppendLine("- **Team Leads & Managers:** Monitor progress, review documentation, and track completion.");
            sb.AppendLine();
            sb.AppendLine("## 4. Core Features");
            sb.AppendLine("- Streamlined workflow navigation and multi-stage lifecycle tracking.");
            sb.AppendLine("- Visual screen inspections with metadata validation.");
            sb.AppendLine("- Automated documentation and manual generation.");
            sb.AppendLine();
            sb.AppendLine("## 5. Screen Specifications");
            for (int i = 0; i < screens.Count; i++)
            {
                var (order, caption, summary) = screens[i];
                string name = caption ?? summary.ScreenTitle;
                sb.AppendLine($"### Screen {i + 1}: {name}");
                sb.AppendLine($"- **Purpose:** {summary.Purpose}");
                sb.AppendLine("- **UI Elements:**");
                foreach (var el in summary.VisibleElements)
                {
                    sb.AppendLine($"  - `{el.Name}` ({el.Type}): {el.Description}");
                }
                sb.AppendLine("- **User Actions:**");
                foreach (var action in summary.UserActions)
                {
                    sb.AppendLine($"  - {action}");
                }
                sb.AppendLine($"- **Outcomes & Navigation:** {summary.NavigatesTo}");
                sb.AppendLine();
            }
            sb.AppendLine("## 6. Main User Flows");
            sb.AppendLine("1. Access the workspace and select the desired project.");
            sb.AppendLine("2. Follow the sequenced screens from initial entry to final approval.");
            sb.AppendLine();
            sb.AppendLine("## 7. Validation Rules & System States");
            sb.AppendLine("- Input fields adhere to max length limits.");
            sb.AppendLine("- Empty submissions are refused with actionable feedback.");
            sb.AppendLine();
            sb.AppendLine("## 8. Roles & Permissions");
            sb.AppendLine("- **Viewer:** Read-only access to approved specifications.");
            sb.AppendLine("- **Contributor / Admin:** Full creation, editing, and generation rights.");
            sb.AppendLine();
            sb.AppendLine("## 9. Glossary");
            sb.AppendLine($"- **{appName}**: Software product under analysis.");
            sb.AppendLine("- **Docset**: Sequenced screen collection representing a product or workflow.");
            sb.AppendLine();
            sb.AppendLine("## 10. Open Questions");
            sb.AppendLine("- Backend API timeouts under extreme batch conditions.");
            sb.AppendLine("- Long-term archival policy for historical document versions.");
        }

        return Task.FromResult(sb.ToString().Trim());
    }
}
