using System.Collections.Concurrent;
using System.Text.Json;
using QaDocBackend.Models;
using QaDocBackend.Repositories;

namespace QaDocBackend.Infrastructure;

public interface IQcJobService
{
    string StartGenerationJob(
        int docSetId, string kind, bool reReadScreens, int userId,
        CancellationToken callerCt = default);

    QcJobStatus? GetJobStatus(string jobId);
}

public class QcJobService(
    IServiceScopeFactory scopeFactory,
    IDocumentGenerator generator,
    DocumentGeneratorSettings settings,
    ILogger<QcJobService> logger) : IQcJobService
{
    private static readonly ConcurrentDictionary<string, QcJobStatus> Jobs = new();

    public string StartGenerationJob(
        int docSetId, string kind, bool reReadScreens, int userId,
        CancellationToken callerCt = default)
    {
        string jobId = Guid.NewGuid().ToString("n");
        var status = new QcJobStatus
        {
            JobId = jobId,
            DocSetId = docSetId,
            Kind = kind,
            Status = "Pending",
            Progress = "Starting generation...",
            CurrentStep = 0,
            TotalSteps = 1
        };
        Jobs[jobId] = status;

        // Run background pipeline
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var qcRepo = scope.ServiceProvider.GetRequiredService<IQcRepository>();
            var attachRepo = scope.ServiceProvider.GetRequiredService<IAttachmentRepository>();

            try
            {
                status.Status = "Running";

                var docSet = await qcRepo.GetByIdAsync(docSetId);
                if (docSet == null)
                {
                    status.Status = "Failed";
                    status.Error = $"Docset #{docSetId} does not exist.";
                    return;
                }

                if (docSet.Screens.Count == 0)
                {
                    status.Status = "Failed";
                    status.Error = "The docset has no screenshots. Please add screenshots first.";
                    return;
                }

                int totalScreens = docSet.Screens.Count;
                status.TotalSteps = totalScreens + 1; // screens + final doc generation

                var parsedSummaries = new List<(int sortOrder, string? caption, QcScreenSummary summary)>();
                var runningSummarySb = new System.Text.StringBuilder();

                // Phase 1: Sequential screen analysis
                for (int i = 0; i < totalScreens; i++)
                {
                    var screen = docSet.Screens[i];
                    status.CurrentStep = i + 1;
                    status.Progress = $"Reading screen {i + 1} of {totalScreens}: \"{screen.Caption ?? $"Screen {i + 1}"}\"...";

                    QcScreenSummary summary;
                    bool hasExisting = !reReadScreens && !string.IsNullOrWhiteSpace(screen.ScreenSummary);

                    if (hasExisting)
                    {
                        try
                        {
                            summary = JsonSerializer.Deserialize<QcScreenSummary>(
                                screen.ScreenSummary!,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                        }
                        catch
                        {
                            hasExisting = false;
                            summary = null!;
                        }
                    }
                    else
                    {
                        summary = null!;
                    }

                    if (!hasExisting)
                    {
                        var attachment = await attachRepo.GetContentAsync(screen.AttachmentId);
                        if (attachment == null)
                        {
                            status.Status = "Failed";
                            status.Error = $"Screenshot #{screen.ScreenId} attachment data is missing.";
                            return;
                        }

                        // Resize image before sending
                        byte[] processedBytes = TestCaseImages.FitToWidth(attachment.Content, settings.MaxImageWidth);
                        var genImage = new GenerateImage(attachment.FileName, attachment.ContentType, processedBytes);

                        // Call vision AI with 1 retry on parse failure
                        try
                        {
                            summary = await generator.AnalyzeScreenAsync(
                                genImage, screen.Caption, docSet.BusinessDescription,
                                runningSummarySb.ToString(), docSet.Language, CancellationToken.None);
                        }
                        catch (Exception ex) when (ex is not DocumentGeneratorException)
                        {
                            logger.LogWarning(ex, "Screen {Index} ({ScreenId}) failed on first attempt; retrying once...", i + 1, screen.ScreenId);
                            try
                            {
                                summary = await generator.AnalyzeScreenAsync(
                                    genImage, screen.Caption, docSet.BusinessDescription,
                                    runningSummarySb.ToString(), docSet.Language, CancellationToken.None);
                            }
                            catch (Exception retryEx)
                            {
                                status.Status = "Failed";
                                status.Error = $"Screen {i + 1} (\"{screen.Caption ?? $"Screen {i + 1}"}\") analysis failed: {retryEx.Message}";
                                return;
                            }
                        }

                        // Save screen summary immediately to database
                        string json = JsonSerializer.Serialize(summary);
                        await qcRepo.UpdateScreenSummaryAsync(screen.ScreenId, json);
                    }

                    parsedSummaries.Add((screen.SortOrder, screen.Caption, summary));
                    runningSummarySb.AppendLine($"Screen {i + 1} ({summary.ScreenTitle}): {summary.Purpose}");
                }

                // Phase 2: Final text-only document generation
                status.CurrentStep = totalScreens + 1;
                status.Progress = $"Generating {kind.ToLowerInvariant()} markdown...";

                string markdown = await generator.GenerateDocumentMarkdownAsync(
                    kind, docSet.AppName, docSet.BusinessDescription,
                    parsedSummaries, docSet.Language, CancellationToken.None);

                if (string.IsNullOrWhiteSpace(markdown))
                {
                    status.Status = "Failed";
                    status.Error = "The AI returned an empty document.";
                    return;
                }

                var createdDoc = await qcRepo.CreateDocumentVersionAsync(
                    docSetId, kind, markdown, QcDocumentSources.Ai, userId, QcDocumentStatuses.Draft);

                status.Status = "Completed";
                status.Progress = "Document generated successfully.";
                status.DocumentId = createdDoc.DocumentId;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "QC Generation job {JobId} for DocSet {DocSetId} failed", jobId, docSetId);
                status.Status = "Failed";
                status.Error = ex.Message;
            }
        });

        return jobId;
    }

    public QcJobStatus? GetJobStatus(string jobId) =>
        Jobs.TryGetValue(jobId, out var status) ? status : null;
}
