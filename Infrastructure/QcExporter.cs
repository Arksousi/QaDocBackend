using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using QaDocBackend.Models;

namespace QaDocBackend.Infrastructure;

public static class QcExporter
{
    // ---------- Markdown Export ----------

    public static byte[] ExportMarkdown(QcDocument document, QcDocSet docSet)
    {
        return Encoding.UTF8.GetBytes(document.Markdown);
    }

    // ---------- HTML Export ----------

    public static string ExportHtml(
        QcDocument document, QcDocSet docSet,
        List<(string caption, string contentType, byte[] bytes)> screenshots,
        (string contentType, byte[] bytes)? logo)
    {
        bool isRtl = string.Equals(docSet.Language, "ar", StringComparison.OrdinalIgnoreCase);
        string dir = isRtl ? "rtl" : "ltr";
        string lang = string.IsNullOrWhiteSpace(docSet.Language) ? "en" : docSet.Language;

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine($"<html lang=\"{lang}\" dir=\"{dir}\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"<title>{System.Net.WebUtility.HtmlEncode(docSet.AppName)} - {System.Net.WebUtility.HtmlEncode(document.Kind)} v{document.Version}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("""
            :root {
                --primary: #2563eb;
                --text: #1e293b;
                --text-muted: #64748b;
                --border: #e2e8f0;
                --bg: #ffffff;
                --surface: #f8fafc;
                --font: system-ui, -apple-system, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            }
            [dir="rtl"] {
                --font: 'Segoe UI', Tahoma, 'Amiri', Arial, sans-serif;
            }
            * { box-sizing: border-box; margin: 0; padding: 0; }
            body {
                font-family: var(--font);
                color: var(--text);
                background: var(--bg);
                line-height: 1.65;
                padding: 40px;
                max-width: 900px;
                margin: 0 auto;
            }
            .header-banner {
                display: flex;
                align-items: center;
                gap: 20px;
                padding-bottom: 24px;
                margin-bottom: 32px;
                border-bottom: 2px solid var(--border);
            }
            .logo-img {
                max-height: 64px;
                max-width: 140px;
                object-fit: contain;
            }
            .title-info h1 {
                font-size: 28px;
                color: var(--primary);
                margin-bottom: 4px;
            }
            .title-info .meta {
                font-size: 14px;
                color: var(--text-muted);
            }
            .doc-body h1 { font-size: 24px; margin: 32px 0 16px; color: var(--primary); border-bottom: 1px solid var(--border); padding-bottom: 8px; }
            .doc-body h2 { font-size: 20px; margin: 24px 0 12px; color: var(--text); }
            .doc-body h3 { font-size: 16px; margin: 18px 0 8px; color: var(--text); }
            .doc-body p { margin-bottom: 14px; }
            .doc-body ul, .doc-body ol { margin: 0 0 16px 24px; }
            [dir="rtl"] .doc-body ul, [dir="rtl"] .doc-body ol { margin: 0 24px 16px 0; }
            .doc-body li { margin-bottom: 6px; }
            .doc-body code {
                font-family: monospace;
                background: var(--surface);
                padding: 2px 6px;
                border-radius: 4px;
                font-size: 0.9em;
            }
            .screen-gallery {
                margin: 40px 0;
                page-break-inside: avoid;
            }
            .screen-card {
                background: var(--surface);
                border: 1px solid var(--border);
                border-radius: 8px;
                padding: 16px;
                margin-bottom: 24px;
                page-break-inside: avoid;
            }
            .screen-card h4 {
                font-size: 15px;
                margin-bottom: 10px;
                color: var(--primary);
            }
            .screen-card img {
                width: 100%;
                max-height: 500px;
                object-fit: contain;
                border: 1px solid var(--border);
                border-radius: 6px;
                background: #fff;
            }
            @media print {
                body { padding: 0; max-width: 100%; }
                .screen-card, h1, h2, h3 { page-break-inside: avoid; }
                .screen-gallery { page-break-before: auto; }
            }
            """);
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        // Header banner with logo
        sb.AppendLine("<div class=\"header-banner\">");
        if (logo.HasValue && logo.Value.bytes.Length > 0)
        {
            string logoB64 = Convert.ToBase64String(logo.Value.bytes);
            sb.AppendLine($"<img class=\"logo-img\" src=\"data:{logo.Value.contentType};base64,{logoB64}\" alt=\"{System.Net.WebUtility.HtmlEncode(docSet.AppName)} logo\">");
        }
        sb.AppendLine("<div class=\"title-info\">");
        sb.AppendLine($"<h1>{System.Net.WebUtility.HtmlEncode(docSet.AppName)}</h1>");
        sb.AppendLine($"<div class=\"meta\">{System.Net.WebUtility.HtmlEncode(document.Kind)} · Version {document.Version} · {document.CreatedAt:yyyy-MM-dd}</div>");
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        // Document markdown rendered as basic clean HTML
        sb.AppendLine("<div class=\"doc-body\">");
        sb.AppendLine(MarkdownToHtml(document.Markdown));
        sb.AppendLine("</div>");

        // Screen gallery section with embedded screenshots
        if (screenshots.Count > 0)
        {
            sb.AppendLine("<div class=\"screen-gallery\">");
            sb.AppendLine("<h2>Screen References</h2>");
            for (int i = 0; i < screenshots.Count; i++)
            {
                var (caption, contentType, bytes) = screenshots[i];
                string b64 = Convert.ToBase64String(bytes);
                sb.AppendLine("<div class=\"screen-card\">");
                sb.AppendLine($"<h4>Screen {i + 1}: {System.Net.WebUtility.HtmlEncode(caption)}</h4>");
                sb.AppendLine($"<img src=\"data:{contentType};base64,{b64}\" alt=\"{System.Net.WebUtility.HtmlEncode(caption)}\">");
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    // ---------- DOCX Export ----------

    public static byte[] ExportDocx(
        QcDocument document, QcDocSet docSet,
        List<(string caption, byte[] bytes)> screenshots,
        byte[]? logoBytes)
    {
        bool isRtl = string.Equals(docSet.Language, "ar", StringComparison.OrdinalIgnoreCase);
        string fontName = isRtl ? "Segoe UI" : "Calibri";

        using var mem = new MemoryStream();
        using (var wordDoc = WordprocessingDocument.Create(mem, WordprocessingDocumentType.Document, true))
        {
            var mainPart = wordDoc.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = new Body();
            mainPart.Document.Append(body);

            // Title Page
            if (logoBytes != null && logoBytes.Length > 0)
            {
                try
                {
                    var logoPart = mainPart.AddImagePart(ImagePartType.Png);
                    using var logoStream = new MemoryStream(logoBytes);
                    logoPart.FeedData(logoStream);
                    string logoRelId = mainPart.GetIdOfPart(logoPart);
                    body.Append(CreateImageParagraph(logoRelId, 2_000_000, 1_000_000, isRtl));
                }
                catch { }
            }

            body.Append(CreateParagraph(docSet.AppName, fontSize: 52, isBold: true, color: "2563EB", isRtl: isRtl, spaceAfter: 200, font: fontName));
            body.Append(CreateParagraph($"{document.Kind} — Version {document.Version}", fontSize: 32, isBold: false, color: "64748B", isRtl: isRtl, spaceAfter: 100, font: fontName));
            body.Append(CreateParagraph($"Project: {docSet.ProjectName} ({docSet.ProjectCode}) · Date: {DateTime.UtcNow:yyyy-MM-dd}", fontSize: 20, isBold: false, color: "94A3B8", isRtl: isRtl, spaceAfter: 600, font: fontName));

            // Section break / page break
            body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));

            // Content lines
            var lines = document.Markdown.Split(['\r', '\n'], StringSplitOptions.None);
            foreach (var rawLine in lines)
            {
                string line = rawLine.TrimEnd();
                if (string.IsNullOrWhiteSpace(line))
                {
                    body.Append(CreateParagraph("", fontSize: 16, spaceAfter: 100, font: fontName));
                    continue;
                }

                if (line.StartsWith("# "))
                {
                    body.Append(CreateParagraph(line[2..].Trim(), fontSize: 36, isBold: true, color: "2563EB", isRtl: isRtl, spaceBefore: 300, spaceAfter: 150, font: fontName));
                }
                else if (line.StartsWith("## "))
                {
                    body.Append(CreateParagraph(line[3..].Trim(), fontSize: 28, isBold: true, color: "1E293B", isRtl: isRtl, spaceBefore: 250, spaceAfter: 120, font: fontName));
                }
                else if (line.StartsWith("### "))
                {
                    body.Append(CreateParagraph(line[4..].Trim(), fontSize: 24, isBold: true, color: "334155", isRtl: isRtl, spaceBefore: 200, spaceAfter: 100, font: fontName));
                }
                else if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    body.Append(CreateParagraph("• " + line[2..].Trim(), fontSize: 22, isRtl: isRtl, spaceAfter: 80, indentLeft: 360, font: fontName));
                }
                else if (char.IsDigit(line[0]) && line.Contains(". "))
                {
                    body.Append(CreateParagraph(line, fontSize: 22, isRtl: isRtl, spaceAfter: 80, indentLeft: 360, font: fontName));
                }
                else
                {
                    body.Append(CreateParagraph(line, fontSize: 22, isRtl: isRtl, spaceAfter: 120, font: fontName));
                }
            }

            // Embedded screenshots section
            if (screenshots.Count > 0)
            {
                body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                body.Append(CreateParagraph("Screen References", fontSize: 32, isBold: true, color: "2563EB", isRtl: isRtl, spaceBefore: 300, spaceAfter: 200, font: fontName));

                for (int i = 0; i < screenshots.Count; i++)
                {
                    var (caption, bytes) = screenshots[i];
                    body.Append(CreateParagraph($"Screen {i + 1}: {caption}", fontSize: 24, isBold: true, color: "334155", isRtl: isRtl, spaceBefore: 200, spaceAfter: 100, font: fontName));

                    if (bytes.Length > 0)
                    {
                        try
                        {
                            var imgPart = mainPart.AddImagePart(ImagePartType.Png);
                            using var imgStream = new MemoryStream(bytes);
                            imgPart.FeedData(imgStream);
                            string relId = mainPart.GetIdOfPart(imgPart);
                            body.Append(CreateImageParagraph(relId, 5_000_000, 3_000_000, isRtl));
                        }
                        catch { }
                    }
                }
            }

            mainPart.Document.Save();
        }

        return mem.ToArray();
    }

    // ---------- Wordprocessing Helpers ----------

    private static Paragraph CreateParagraph(
        string text, int fontSize = 22, bool isBold = false,
        string? color = null, bool isRtl = false,
        int spaceBefore = 0, int spaceAfter = 100,
        int indentLeft = 0, string font = "Calibri")
    {
        var p = new Paragraph();
        var pPr = new ParagraphProperties();

        if (spaceBefore > 0 || spaceAfter > 0)
        {
            pPr.SpacingBetweenLines = new SpacingBetweenLines
            {
                Before = spaceBefore.ToString(),
                After = spaceAfter.ToString()
            };
        }

        if (indentLeft > 0)
        {
            pPr.Indentation = new Indentation { Left = indentLeft.ToString() };
        }

        if (isRtl)
        {
            pPr.BiDi = new BiDi();
            pPr.Justification = new Justification { Val = JustificationValues.Right };
        }

        p.Append(pPr);

        if (!string.IsNullOrEmpty(text))
        {
            var run = new Run();
            var rPr = new RunProperties();
            rPr.RunFonts = new RunFonts { Ascii = font, HighAnsi = font, ComplexScript = font };
            rPr.FontSize = new FontSize { Val = fontSize.ToString() };
            if (isBold) rPr.Bold = new Bold();
            if (!string.IsNullOrEmpty(color)) rPr.Color = new Color { Val = color };
            if (isRtl) rPr.RightToLeftText = new RightToLeftText();

            run.Append(rPr);
            run.Append(new Text(text));
            p.Append(run);
        }

        return p;
    }

    private static Paragraph CreateImageParagraph(string relationshipId, long widthEmu, long heightEmu, bool isRtl)
    {
        var element = new Drawing(
            new DW.Inline(
                new DW.Extent { Cx = widthEmu, Cy = heightEmu },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = (UInt32Value)1U, Name = "Picture" },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = (UInt32Value)0U, Name = "Image" },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip(new A.BlipExtensionList(new A.BlipExtension { Uri = "{28A0092B-C50C-407E-A947-70E740481C1C}" }))
                                {
                                    Embed = relationshipId,
                                    CompressionState = A.BlipCompressionValues.Print
                                },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0L, Y = 0L },
                                    new A.Extents { Cx = widthEmu, Cy = heightEmu }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }))
                    )
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }
                )
            )
            {
                DistanceFromTop = (UInt32Value)0U,
                DistanceFromBottom = (UInt32Value)0U,
                DistanceFromLeft = (UInt32Value)0U,
                DistanceFromRight = (UInt32Value)0U
            });

        var p = new Paragraph(new ParagraphProperties(
            new SpacingBetweenLines { After = "200" },
            isRtl ? new BiDi() : null!));
        p.Append(new Run(element));
        return p;
    }

    private static string MarkdownToHtml(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        var sb = new StringBuilder();
        var lines = markdown.Split(['\r', '\n'], StringSplitOptions.None);
        bool inList = false;

        foreach (var line in lines)
        {
            string trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (inList) { sb.AppendLine("</ul>"); inList = false; }
                continue;
            }

            if (trimmed.StartsWith("### "))
            {
                if (inList) { sb.AppendLine("</ul>"); inList = false; }
                sb.AppendLine($"<h3>{System.Net.WebUtility.HtmlEncode(trimmed[4..].Trim())}</h3>");
            }
            else if (trimmed.StartsWith("## "))
            {
                if (inList) { sb.AppendLine("</ul>"); inList = false; }
                sb.AppendLine($"<h2>{System.Net.WebUtility.HtmlEncode(trimmed[3..].Trim())}</h2>");
            }
            else if (trimmed.StartsWith("# "))
            {
                if (inList) { sb.AppendLine("</ul>"); inList = false; }
                sb.AppendLine($"<h1>{System.Net.WebUtility.HtmlEncode(trimmed[2..].Trim())}</h1>");
            }
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                if (!inList) { sb.AppendLine("<ul>"); inList = true; }
                sb.AppendLine($"<li>{FormatInlineMarkdown(trimmed[2..].Trim())}</li>");
            }
            else
            {
                if (inList) { sb.AppendLine("</ul>"); inList = false; }
                sb.AppendLine($"<p>{FormatInlineMarkdown(trimmed)}</p>");
            }
        }

        if (inList) sb.AppendLine("</ul>");
        return sb.ToString();
    }

    private static string FormatInlineMarkdown(string text)
    {
        string encoded = System.Net.WebUtility.HtmlEncode(text);
        // Replace bold **text**
        encoded = System.Text.RegularExpressions.Regex.Replace(encoded, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        // Replace code `text`
        encoded = System.Text.RegularExpressions.Regex.Replace(encoded, @"`(.+?)`", "<code>$1</code>");
        return encoded;
    }
}
