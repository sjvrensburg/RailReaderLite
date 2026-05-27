// PDF.js shim for RailReaderLite.
//
// Wraps pdf.js in a small API designed for [JSImport] marshalling:
// every exported function returns either a primitive, a `byte[]`
// (Uint8ClampedArray / Uint8Array), or a JSON string. No JSObject
// out-parameters, no nested typed-array structures — those don't
// marshal cleanly across the C#/JS boundary in net10.0-browser.
//
// The C# side (see Interop/PdfJsInterop.cs in this project) calls
// these via JSImport and `System.Text.Json` deserialisation.

import * as pdfjsLib from './lib/pdfjs/pdf.min.mjs';

pdfjsLib.GlobalWorkerOptions.workerSrc = './lib/pdfjs/pdf.worker.min.mjs';

const docs = new Map();
let nextDocId = 1;

// Per-call slot for the last render. Safe because MainViewModel
// coalesces renders to one at a time, and the C# side reads these
// synchronously immediately after awaiting `renderPage` — no JS
// interleaving can happen between those two calls. We split the
// async work from the buffer fetch because the JSImport source
// generator doesn't support `Task<byte[]>` as a return type
// (SYSLIB1072), but supports `Task` and sync `byte[]` separately.
let lastRenderW = 0;
let lastRenderH = 0;
let lastRenderBuffer = null;  // Uint8ClampedArray

async function openDoc(bytes) {
    const data = new Uint8Array(bytes);
    const proxy = await pdfjsLib.getDocument({ data }).promise;
    const id = nextDocId++;
    docs.set(id, { proxy, pageCache: new Map(), textCache: new Map() });
    return JSON.stringify({ docId: id, pageCount: proxy.numPages });
}

function closeDoc(docId) {
    const entry = docs.get(docId);
    if (!entry) return;
    try { entry.proxy.destroy(); } catch (_) { /* ignore */ }
    docs.delete(docId);
}

async function getPage(docId, pageIndex) {
    const entry = docs.get(docId);
    if (!entry) throw new Error(`Unknown docId ${docId}`);
    const pageNo = pageIndex + 1;  // pdf.js is 1-indexed
    let page = entry.pageCache.get(pageNo);
    if (!page) {
        page = await entry.proxy.getPage(pageNo);
        entry.pageCache.set(pageNo, page);
    }
    return page;
}

async function getPageSize(docId, pageIndex) {
    const page = await getPage(docId, pageIndex);
    const viewport = page.getViewport({ scale: 1.0 });
    return JSON.stringify({ width: viewport.width, height: viewport.height });
}

// Renders the page into an OffscreenCanvas at the requested longest-
// edge pixel count, then stashes the result in the per-call slot.
// The C# side awaits this `Task` and immediately calls
// `takeRenderBuffer` + `lastRenderWidth/Height` to retrieve the
// result.
async function renderPage(docId, pageIndex, targetLongestEdge) {
    const page = await getPage(docId, pageIndex);
    const baseViewport = page.getViewport({ scale: 1.0 });
    const scale = targetLongestEdge / Math.max(baseViewport.width, baseViewport.height);
    const viewport = page.getViewport({ scale });

    const width  = Math.ceil(viewport.width);
    const height = Math.ceil(viewport.height);

    const canvas = new OffscreenCanvas(width, height);
    const ctx = canvas.getContext('2d', { alpha: false });
    await page.render({ canvasContext: ctx, viewport }).promise;

    const imageData = ctx.getImageData(0, 0, width, height);
    lastRenderW = width;
    lastRenderH = height;
    // imageData.data is Uint8ClampedArray, length 4 * width * height.
    // We need a Uint8Array view for JSImport's byte[] marshalling —
    // Uint8ClampedArray works but Uint8Array is the canonical mapping.
    lastRenderBuffer = new Uint8Array(imageData.data.buffer, imageData.data.byteOffset, imageData.data.byteLength);
}

function takeRenderBuffer() {
    const b = lastRenderBuffer;
    lastRenderBuffer = null;
    return b;
}
function lastRenderWidth()  { return lastRenderW; }
function lastRenderHeight() { return lastRenderH; }

// Text extraction. Returns a JSON string with an array of items in
// page-point space (origin top-left, Y-down). Each item is a word-
// level run as produced by pdf.js getTextContent().
async function getTextItemsJson(docId, pageIndex) {
    const entry = docs.get(docId);
    if (!entry) throw new Error(`Unknown docId ${docId}`);
    let cached = entry.textCache.get(pageIndex);
    if (cached) return cached;

    const page = await getPage(docId, pageIndex);
    const pageH = page.getViewport({ scale: 1.0 }).height;

    const tc = await page.getTextContent({
        includeMarkedContent: false,
        disableNormalization: false,
    });

    const items = [];
    for (const item of tc.items) {
        if (item.type !== undefined && item.type !== 'string') continue;
        const str = item.str ?? '';
        // pdf.js emits zero-width items as EOL markers. Skip them — the
        // word-level DLA derives line boundaries from geometry, not
        // these markers.
        if (str.length === 0) continue;

        const [, , , , tx, ty] = item.transform;
        const w = item.width;
        const h = item.height;
        // (tx, ty) is the baseline origin in Y-up. Convert to Y-down
        // top-left rect.
        const top    = pageH - ty - h;
        const bottom = pageH - ty;
        items.push({
            s: str,
            x: tx,
            y: top,
            w: w,
            h: bottom - top,
        });
    }
    const json = JSON.stringify(items);
    entry.textCache.set(pageIndex, json);
    return json;
}

// Outline as a JSON string. Each entry: {title, pageIndex, children[]}.
async function getOutlineJson(docId) {
    const entry = docs.get(docId);
    if (!entry) throw new Error(`Unknown docId ${docId}`);
    const outline = await entry.proxy.getOutline();
    if (!outline) return '[]';

    async function flatten(entries) {
        const out = [];
        for (const e of entries) {
            let pageIndex = -1;
            try {
                let dest = e.dest;
                if (typeof dest === 'string') {
                    dest = await entry.proxy.getDestination(dest);
                }
                if (Array.isArray(dest) && dest.length > 0) {
                    const ref = dest[0];
                    if (ref) pageIndex = await entry.proxy.getPageIndex(ref);
                }
            } catch (_) { /* unresolved — leave -1 */ }
            const children = (e.items && e.items.length > 0)
                ? await flatten(e.items)
                : [];
            out.push({ title: e.title ?? '', pageIndex, children });
        }
        return out;
    }

    return JSON.stringify(await flatten(outline));
}

globalThis.RailReaderPdfJs = {
    openDoc,
    closeDoc,
    getPageSize,
    renderPage,
    takeRenderBuffer,
    lastRenderWidth,
    lastRenderHeight,
    getTextItemsJson,
    getOutlineJson,
};
