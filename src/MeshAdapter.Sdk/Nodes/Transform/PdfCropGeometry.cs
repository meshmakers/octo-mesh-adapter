using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>One output-page operation consumed by <see cref="TransformPdfNode"/>.</summary>
public sealed record PdfPageOp
{
    /// <summary>0-based index into the base64 source-PDF array.</summary>
    public int SourceIndex { get; init; }

    /// <summary>0-based page within the selected source.</summary>
    public int PageIndex { get; init; }

    /// <summary>Clockwise degrees added on top of the page's existing rotation (0/90/180/270).</summary>
    public int Rotate { get; init; }

    /// <summary>Optional crop rectangle; null or zero-size means no crop.</summary>
    public PdfCropRect? Crop { get; init; }
}

/// <summary>
/// A crop rectangle normalized to [0,1] with a top-left origin, expressed in the page's
/// FINAL displayed orientation (after existing rotation + the op's rotation) — the same
/// orientation the editor renders the page in.
/// </summary>
public sealed record PdfCropRect
{
    /// <summary>Left edge, normalized [0,1] from the left.</summary>
    public double X { get; init; }

    /// <summary>Top edge, normalized [0,1] from the top.</summary>
    public double Y { get; init; }

    /// <summary>Width, normalized [0,1] of the displayed page width.</summary>
    public double Width { get; init; }

    /// <summary>Height, normalized [0,1] of the displayed page height.</summary>
    public double Height { get; init; }
}

/// <summary>
/// Maps a crop rectangle drawn in a page's displayed (rotated) orientation back into the
/// page's unrotated PDF coordinate space, so it can be applied as a <c>CropBox</c> while a
/// separate <c>/Rotate</c> handles the display rotation. Pure and side-effect free so the
/// geometry is unit-testable in isolation.
/// </summary>
public static class PdfCropGeometry
{
    /// <summary>
    /// Converts a display-space normalized crop rect into an absolute PDF <see cref="PdfRectangle"/>
    /// (bottom-left origin) in the page's unrotated coordinate system.
    /// </summary>
    /// <param name="mediaBox">The page's unrotated media box.</param>
    /// <param name="displayRotation">The page's final display rotation (0/90/180/270, clockwise).</param>
    /// <param name="rect">The crop rect in display space (normalized, top-left origin).</param>
    public static PdfRectangle DisplayRectToCropBox(PdfRectangle mediaBox, int displayRotation, PdfCropRect rect)
    {
        var dx0 = Clamp01(rect.X);
        var dy0 = Clamp01(rect.Y);
        var dx1 = Clamp01(rect.X + rect.Width);
        var dy1 = Clamp01(rect.Y + rect.Height);

        // Map both corners from the displayed orientation back into the page's unrotated
        // normalized space (top-left origin, y-down).
        var (ax, ay) = DisplayToUnrotated(dx0, dy0, displayRotation);
        var (bx, by) = DisplayToUnrotated(dx1, dy1, displayRotation);

        var nx0 = Math.Min(ax, bx);
        var nx1 = Math.Max(ax, bx);
        var ny0 = Math.Min(ay, by); // top edge (top-left origin)
        var ny1 = Math.Max(ay, by); // bottom edge

        var w = Math.Abs(mediaBox.Width);
        var h = Math.Abs(mediaBox.Height);
        var originX = Math.Min(mediaBox.X1, mediaBox.X2);
        var originY = Math.Min(mediaBox.Y1, mediaBox.Y2);

        // Absolute PDF coords (bottom-left origin, y-up).
        var llx = originX + nx0 * w;
        var urx = originX + nx1 * w;
        var lly = originY + (1 - ny1) * h;
        var ury = originY + (1 - ny0) * h;

        return new PdfRectangle(new XPoint(llx, lly), new XPoint(urx, ury));
    }

    /// <summary>
    /// Maps a point given in a page's displayed orientation (normalized, top-left origin,
    /// y-down) back to the page's unrotated normalized space, undoing a clockwise
    /// <paramref name="rotation"/>.
    /// </summary>
    public static (double X, double Y) DisplayToUnrotated(double dx, double dy, int rotation)
    {
        return rotation switch
        {
            90 => (dy, 1 - dx),
            180 => (1 - dx, 1 - dy),
            270 => (1 - dy, dx),
            _ => (dx, dy)
        };
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
