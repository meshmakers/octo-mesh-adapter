using System.Runtime.InteropServices;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Runs the IronOCR calls of <see cref="PdfOcrExtractionNode"/> one at a time per process and hands the
/// native memory a call leaves behind back to the operating system afterwards (AB#8407).
/// <para>
/// IronOCR (Tesseract, Leptonica, PDF rasterisation) allocates its page buffers with glibc <c>malloc</c> on
/// up to one worker thread per core. When a call has finished and disposed its input, that memory is free
/// inside the process but glibc keeps the pages in its per-thread arenas, so the resident set of the
/// adapter only ever grows: on prod-1 ~0.3-0.4 GB per batch of OCR passes until the pod was OOM-killed
/// at 3 GiB (AB#8407, AB#5449). <c>malloc_trim(0)</c> after each call returns those pages. Measured in a
/// Linux container (glibc, amd64), 25 OCR passes on scanned PDFs/PNGs: ~0.9-1.35 GB native without it,
/// ~0.3 GB flat with it; a forced GC alone, a reused engine or <c>MALLOC_ARENA_MAX=2</c> did not get close.
/// </para>
/// <para>
/// The calls are serialised because a trim can only return what no other call still uses, and because
/// one IronOCR call already uses every core for its pages, so parallel calls mainly multiply the native
/// buffers held at the same time.
/// </para>
/// <para>
/// octo-communication-sdk has the same trim for script compiles (<c>ScriptCompileCoordinator</c>, AB#7063),
/// but it is internal to that package, so the few lines are repeated here.
/// </para>
/// </summary>
internal sealed class OcrNativeMemoryCoordinator
{
    /// <summary>The instance used by <see cref="PdfOcrExtractionNode"/>.</summary>
    internal static OcrNativeMemoryCoordinator Shared { get; } = new(() => TryMallocTrim());

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action _releaseNativeMemory;

    internal OcrNativeMemoryCoordinator(Action releaseNativeMemory)
    {
        _releaseNativeMemory = releaseNativeMemory;
    }

    /// <summary>
    /// Runs <paramref name="ocr"/> exclusively (one at a time per coordinator), then releases native memory,
    /// also when <paramref name="ocr"/> throws. <paramref name="ocr"/> must dispose its OCR input itself, so the
    /// buffers are free before the release runs.
    /// </summary>
    internal async Task<T> RunAsync<T>(Func<T> ocr)
    {
        // Awaited, not blocked: callers queued behind a running OCR call must not hold thread-pool workers.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return ocr();
        }
        finally
        {
            try
            {
                _releaseNativeMemory();
            }
            catch
            {
                // Releasing memory is best effort and must never change the outcome of an OCR call.
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MallocTrimDelegate(nuint pad);

    // Resolved once: dlopen/dlsym per call would only add a library reference each time.
    private static readonly Lazy<MallocTrimDelegate?> MallocTrim = new(ResolveMallocTrim);

    /// <summary>
    /// Calls glibc <c>malloc_trim(0)</c>. Returns false (never throws) off Linux, on musl, or when the symbol
    /// cannot be resolved.
    /// </summary>
    internal static bool TryMallocTrim()
    {
        try
        {
            var trim = MallocTrim.Value;
            if (trim is null)
            {
                return false;
            }

            trim(0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static MallocTrimDelegate? ResolveMallocTrim()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return NativeLibrary.TryLoad("libc.so.6", out var libc)
                   && NativeLibrary.TryGetExport(libc, "malloc_trim", out var export)
                ? Marshal.GetDelegateForFunctionPointer<MallocTrimDelegate>(export)
                : null;
        }
        catch
        {
            return null;
        }
    }
}
