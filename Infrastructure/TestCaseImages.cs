using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace QaDocBackend.Infrastructure;

/// <summary>
/// The last thing that happens to a screenshot before it leaves the building: anything wider than
/// the configured maximum is scaled down first, so a huge paste cannot be shipped to the model
/// (or billed for) at full size. Images already small enough travel untouched.
/// </summary>
public static class TestCaseImages
{
    /// <summary>
    /// Returns the bytes to send: the original when it already fits (or cannot be decoded), a
    /// re-encoded copy when it does not. Never throws — a picture that cannot be read is simply
    /// sent as it arrived, and the model decides what it can make of it.
    /// </summary>
    public static byte[] FitToWidth(byte[] content, int maxWidth)
    {
        if (maxWidth <= 0 || content.Length == 0) return content;
        try
        {
            var format = Image.DetectFormat(content);
            if (format == null) return content;

            using var image = Image.Load(content);
            if (image.Width <= maxWidth) return content;

            image.Mutate(ctx => ctx.Resize(maxWidth, 0));
            using var stream = new MemoryStream();
            image.Save(stream, format);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException or OutOfMemoryException)
        {
            return content;
        }
    }
}
