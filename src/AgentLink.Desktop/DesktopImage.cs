using AgentLink.Core;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AgentLink.Desktop;

internal static class DesktopImage
{
    internal static byte[] EncodePng(Bitmap bitmap, int maximumBytes = Rules.MaxImageBytes)
    {
        using var stream = new CappedMemoryStream(maximumBytes);
        try { bitmap.Save(stream, ImageFormat.Png); }
        catch (ExternalException) when (stream.LimitExceeded)
        {
            throw new AgentLinkException("capture_too_large", "Encoded PNG exceeds its artifact limit. Request a smaller crop.");
        }
        if (stream.LimitExceeded || stream.Length > maximumBytes)
            throw new AgentLinkException("capture_too_large", "Encoded PNG exceeds its artifact limit.");
        return stream.ToArray();
    }
}
