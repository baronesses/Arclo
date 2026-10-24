using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Orbit.Core;

/// <summary>Permits self-contained SVG geometry and embedded PNGs only. Never enables general SVG delegates or external resources.</summary>
internal static class SvgImagePolicy
{
    private const int MaximumSvgBytes = 64 * 1024 * 1024;
    private const int MaximumEmbeddedPngBytes = 32 * 1024 * 1024;
    private const string SvgNamespace = "http://www.w3.org/2000/svg";
    private const string XlinkNamespace = "http://www.w3.org/1999/xlink";
    private static readonly HashSet<string> Elements = new(StringComparer.Ordinal)
    { "svg", "g", "defs", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "tspan", "title", "desc", "linearGradient", "radialGradient", "stop", "clipPath", "image", "use" };
    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal)
    { "id", "version", "width", "height", "viewBox", "x", "y", "x1", "x2", "y1", "y2", "cx", "cy", "r", "rx", "ry", "fx", "fy", "d", "points", "transform", "fill", "fill-rule", "fill-opacity", "stroke", "stroke-width", "stroke-opacity", "stroke-linecap", "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "opacity", "clip-path", "clip-rule", "clipPathUnits", "preserveAspectRatio", "gradientUnits", "gradientTransform", "spreadMethod", "offset", "stop-color", "stop-opacity", "font-family", "font-size", "font-weight", "font-style", "text-anchor", "dominant-baseline", "letter-spacing", "word-spacing", "dx", "dy", "rotate", "href", "role", "aria-labelledby", "space" };

    public static async Task ValidateAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (new FileInfo(path).Length is <= 0 or > MaximumSvgBytes)
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_C0ED0C15ECB0"));
        try
        {
            var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumSvgBytes, IgnoreComments = true, IgnoreProcessingInstructions = false };
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
            using var reader = XmlReader.Create(file, settings);
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            if (document.Root is not { } root || root.Name != XName.Get("svg", SvgNamespace)
                || document.DescendantNodes().OfType<XProcessingInstruction>().Any())
                throw Unsupported();
            int count = 0;
            foreach (var element in root.DescendantsAndSelf())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > 100000 || element.Name.NamespaceName != SvgNamespace || !Elements.Contains(element.Name.LocalName)) throw Unsupported();
                foreach (var attribute in element.Attributes())
                {
                    if (attribute.IsNamespaceDeclaration)
                    {
                        if (attribute.Value is not (SvgNamespace or XlinkNamespace)) throw Unsupported();
                        continue;
                    }
                    if (!Attributes.Contains(attribute.Name.LocalName)
                        || attribute.Name.NamespaceName.Length != 0 && attribute.Name.NamespaceName is not (XlinkNamespace or "http://www.w3.org/XML/1998/namespace"))
                        throw Unsupported();
                    var value = attribute.Value.Trim();
                    if (attribute.Name.LocalName == "href")
                    {
                        if (value.StartsWith('#') && Regex.IsMatch(value, @"\A#[A-Za-z_][A-Za-z0-9_.:-]*\z", RegexOptions.CultureInvariant)) continue;
                        const string prefix = "data:image/png;base64,";
                        if (element.Name.LocalName != "image" || !value.StartsWith(prefix, StringComparison.Ordinal)
                            || value.Length > prefix.Length + (MaximumEmbeddedPngBytes + 2L) / 3 * 4) throw Unsupported();
                        byte[] png;
                        try { png = Convert.FromBase64String(value[prefix.Length..]); }
                        catch (FormatException) { throw Unsupported(); }
                        ValidatePng(png);
                        continue;
                    }
                    if (value.Contains('\\') || value.Contains("://", StringComparison.Ordinal) || value.Contains("data:", StringComparison.OrdinalIgnoreCase)
                        || value.Contains('@') || value.Contains("url", StringComparison.OrdinalIgnoreCase)
                        && !Regex.IsMatch(value, @"\Aurl\(\s*#[A-Za-z_][A-Za-z0-9_.:-]*\s*\)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        throw Unsupported();
                }
            }
        }
        catch (XmlException exception) { throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_B0F76ECD23C3"), exception); }
    }

    public static async Task WriteContainerAsync(string pngPath, string svgPath, CancellationToken cancellationToken)
    {
        if (new FileInfo(pngPath).Length > MaximumEmbeddedPngBytes)
            throw new ImageProcessingException(global::Orbit.Core.Localization.L.Get("Text_176C820B9BC6"));
        var png = await File.ReadAllBytesAsync(pngPath, cancellationToken).ConfigureAwait(false);
        var (width, height) = ValidatePng(png);
        XNamespace svg = SvgNamespace, xlink = XlinkNamespace;
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(svg + "svg",
            new XAttribute(XNamespace.Xmlns + "xlink", XlinkNamespace), new XAttribute("version", "1.1"),
            new XAttribute("width", width), new XAttribute("height", height),
            new XAttribute("viewBox", FormattableString.Invariant($"0 0 {width} {height}")),
            new XElement(svg + "image", new XAttribute("width", width), new XAttribute("height", height),
                new XAttribute(xlink + "href", "data:image/png;base64," + Convert.ToBase64String(png)))));
        await File.WriteAllTextAsync(svgPath, document.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]?> ReadContainerAsync(string path, CancellationToken cancellationToken)
    {
        // Callers validate the complete document before reaching this helper.
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumSvgBytes };
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        using var reader = XmlReader.Create(stream, settings);
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        XNamespace svg = SvgNamespace, xlink = XlinkNamespace;
        var root = document.Root!;
        if (root.Elements().Count() != 1 || root.Element(svg + "image") is not { } image) return null;
        var value = image.Attribute(xlink + "href")?.Value ?? image.Attribute("href")?.Value;
        const string prefix = "data:image/png;base64,";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var png = Convert.FromBase64String(value[prefix.Length..]);
        var (width, height) = ValidatePng(png);
        // Only the exact raster container is unwrapped. General transformed SVG
        // composition stays with the restricted native MSVG renderer.
        if (root.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.LocalName is not ("version" or "width" or "height" or "viewBox"))
            || image.Attributes().Any(a => a.Name.LocalName is not ("width" or "height" or "href"))
            || root.Attribute("width")?.Value != width.ToString(CultureInfo.InvariantCulture)
            || root.Attribute("height")?.Value != height.ToString(CultureInfo.InvariantCulture)
            || root.Attribute("viewBox")?.Value != FormattableString.Invariant($"0 0 {width} {height}")
            || image.Attribute("width")?.Value != width.ToString(CultureInfo.InvariantCulture)
            || image.Attribute("height")?.Value != height.ToString(CultureInfo.InvariantCulture)) return null;
        return png;
    }

    private static (int Width, int Height) ValidatePng(byte[] png)
    {
        if (png.Length is < 24 or > MaximumEmbeddedPngBytes || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw Unsupported();
        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        if (width <= 0 || height <= 0) throw Unsupported();
        return (width, height);
    }

    private static ImageProcessingException Unsupported() => new(global::Orbit.Core.Localization.L.Get("Text_F96023209CAB"));
}
