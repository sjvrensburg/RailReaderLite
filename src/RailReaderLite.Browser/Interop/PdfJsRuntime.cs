using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using RailReaderLite.Services;

namespace RailReaderLite.Browser.Interop;

/// <summary>
/// Concrete <see cref="IPdfJsRuntime"/> backed by <c>[JSImport]</c>
/// calls into <c>globalThis.RailReaderPdfJs</c> (see
/// <c>wwwroot/pdfjs-shim.mjs</c>). Constructed once on app startup
/// from <c>Program.cs</c> and published via
/// <see cref="PdfJsRuntimeRegistry.Current"/>.
///
/// <para>
/// Marshalling strategy:
/// <list type="bullet">
///   <item><description>Small structured data crosses as JSON strings and
///   deserialises through <see cref="LiteJsonContext"/> (source-gen JSON
///   so the Browser project's trim pass doesn't strip reflection metadata).</description></item>
///   <item><description>The render-page payload is large (millions of
///   bytes). The JSImport source generator can't marshal
///   <c>Task&lt;byte[]&gt;</c> (SYSLIB1072), so we split it into a
///   void <see cref="Task"/> that does the work + stashes into a JS
///   slot, then a sync <c>byte[]</c> fetch + two sync int getters for
///   width/height. Safe because renders are serialised at the VM.</description></item>
/// </list>
/// </para>
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class PdfJsRuntime : IPdfJsRuntime
{
    public async Task<(int DocId, int PageCount)> OpenDocAsync(byte[] bytes)
    {
        var json = await OpenDocJsAsync(bytes);
        var r = JsonSerializer.Deserialize(json, LiteJsonContext.Default.OpenDocResult)
            ?? throw new InvalidOperationException("Failed to parse openDoc result");
        return (r.DocId, r.PageCount);
    }

    public void CloseDoc(int docId) => CloseDocJs(docId);

    public async Task<(double Width, double Height)> GetPageSizeAsync(int docId, int pageIndex)
    {
        var json = await GetPageSizeJsAsync(docId, pageIndex);
        var r = JsonSerializer.Deserialize(json, LiteJsonContext.Default.PageSize)
            ?? throw new InvalidOperationException("Failed to parse getPageSize result");
        return (r.Width, r.Height);
    }

    public async Task<RenderedPage> RenderPageAsync(int docId, int pageIndex, int targetLongestEdge)
    {
        await RenderPageJsAsync(docId, pageIndex, targetLongestEdge);
        byte[] rgba = TakeRenderBufferJs();
        int w = LastRenderWidthJs();
        int h = LastRenderHeightJs();
        return new RenderedPage(rgba, w, h);
    }

    public async Task<IReadOnlyList<PdfTextItem>> GetTextItemsAsync(int docId, int pageIndex)
    {
        var json = await GetTextItemsJsonJsAsync(docId, pageIndex);
        return JsonSerializer.Deserialize(json, LiteJsonContext.Default.ListPdfTextItem)
            ?? new List<PdfTextItem>();
    }

    public async Task<IReadOnlyList<PdfOutlineEntry>> GetOutlineAsync(int docId)
    {
        var json = await GetOutlineJsonJsAsync(docId);
        return JsonSerializer.Deserialize(json, LiteJsonContext.Default.ListPdfOutlineEntry)
            ?? new List<PdfOutlineEntry>();
    }

    // ----- JSImport surface. JS-side functions live in pdfjs-shim.mjs.

    [JSImport("globalThis.RailReaderPdfJs.openDoc")]
    private static partial Task<string> OpenDocJsAsync(byte[] bytes);

    [JSImport("globalThis.RailReaderPdfJs.closeDoc")]
    private static partial void CloseDocJs(int docId);

    [JSImport("globalThis.RailReaderPdfJs.getPageSize")]
    private static partial Task<string> GetPageSizeJsAsync(int docId, int pageIndex);

    [JSImport("globalThis.RailReaderPdfJs.renderPage")]
    private static partial Task RenderPageJsAsync(int docId, int pageIndex, int targetLongestEdge);

    [JSImport("globalThis.RailReaderPdfJs.takeRenderBuffer")]
    private static partial byte[] TakeRenderBufferJs();

    [JSImport("globalThis.RailReaderPdfJs.lastRenderWidth")]
    private static partial int LastRenderWidthJs();

    [JSImport("globalThis.RailReaderPdfJs.lastRenderHeight")]
    private static partial int LastRenderHeightJs();

    [JSImport("globalThis.RailReaderPdfJs.getTextItemsJson")]
    private static partial Task<string> GetTextItemsJsonJsAsync(int docId, int pageIndex);

    [JSImport("globalThis.RailReaderPdfJs.getOutlineJson")]
    private static partial Task<string> GetOutlineJsonJsAsync(int docId);
}

internal sealed record OpenDocResult(
    [property: JsonPropertyName("docId")] int DocId,
    [property: JsonPropertyName("pageCount")] int PageCount);

internal sealed record PageSize(
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height);

[JsonSerializable(typeof(OpenDocResult))]
[JsonSerializable(typeof(PageSize))]
[JsonSerializable(typeof(List<PdfTextItem>))]
[JsonSerializable(typeof(List<PdfOutlineEntry>))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
internal partial class LiteJsonContext : JsonSerializerContext
{
}
