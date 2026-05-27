using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace RailReaderLite.Services;

/// <summary>
/// Abstraction over the JS PDF.js runtime so the platform-agnostic
/// shared project doesn't have to pull in <c>[JSImport]</c> bindings
/// (which only compile against the <c>browser</c> TFM). The Browser
/// entry point constructs a concrete <see cref="IPdfJsRuntime"/> and
/// publishes it via <see cref="PdfJsRuntimeRegistry.Current"/>; the
/// VM reads it through the interface only.
/// </summary>
public interface IPdfJsRuntime
{
    Task<(int DocId, int PageCount)> OpenDocAsync(byte[] bytes);
    void CloseDoc(int docId);
    Task<(double Width, double Height)> GetPageSizeAsync(int docId, int pageIndex);
    Task<RenderedPage> RenderPageAsync(int docId, int pageIndex, int targetLongestEdge);
    Task<IReadOnlyList<PdfTextItem>> GetTextItemsAsync(int docId, int pageIndex);
    Task<IReadOnlyList<PdfOutlineEntry>> GetOutlineAsync(int docId);
}

/// <summary>RGBA byte payload returned by <see cref="IPdfJsRuntime.RenderPageAsync"/>.
/// Width/height are in pixels; <see cref="Rgba"/> length is 4 × W × H.</summary>
public sealed record RenderedPage(byte[] Rgba, int Width, int Height);

/// <summary>A word-level text run as produced by PDF.js's
/// <c>getTextContent()</c>. Coordinates are page-point space
/// (origin top-left, Y-down) — the shim handles the Y-flip from
/// PDF.js's native Y-up convention.</summary>
public sealed record PdfTextItem(
    [property: JsonPropertyName("s")] string Text,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("w")] double Width,
    [property: JsonPropertyName("h")] double Height);

public sealed record PdfOutlineEntry(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("pageIndex")] int PageIndex,
    [property: JsonPropertyName("children")] List<PdfOutlineEntry> Children);

/// <summary>Static slot the Browser entry point fills on startup.
/// Kept in the shared project so the VM can resolve the runtime
/// without taking a project reference to <c>RailReaderLite.Browser</c>
/// (that would invert the existing dependency direction).</summary>
public static class PdfJsRuntimeRegistry
{
    public static IPdfJsRuntime? Current { get; set; }
}
