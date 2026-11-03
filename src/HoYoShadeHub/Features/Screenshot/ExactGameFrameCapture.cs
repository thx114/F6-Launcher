using Microsoft.Graphics.Canvas;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Foundation;

namespace HoYoShadeHub.Features.Screenshot;

/// <summary>A single Windows Graphics Capture frame, without activating or touching the window.</summary>
internal static class ExactGameFrameCapture
{
    public static async Task<byte[]> CapturePngAsync(nint window)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new InvalidOperationException("Windows Graphics Capture is unavailable.");
        GraphicsCaptureItem item = ScreenCaptureHelper.CreateGraphicsCaptureItemForWindow(window);
        if (item is null || item.Size.Width <= 0 || item.Size.Height <= 0)
            throw new InvalidOperationException("Game capture item is unavailable.");

        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            CanvasDevice.GetSharedDevice(), DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
#pragma warning disable CA1416
        if (ScreenCaptureHelper.IsIsBorderRequiredPresent) session.IsBorderRequired = false;
        if (ScreenCaptureHelper.IsIsCursorCaptureEnabledPresent) session.IsCursorCaptureEnabled = false;
#pragma warning restore CA1416

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var completed = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = timeout.Token.Register(() => completed.TrySetException(
            new TimeoutException("No game frame arrived within five seconds.")));
        int consumed = 0;
        pool.FrameArrived += async (sender, _) =>
        {
            if (Interlocked.Exchange(ref consumed, 1) != 0) return;
            try
            {
                using var frame = sender.TryGetNextFrame()
                    ?? throw new InvalidOperationException("The game frame was empty.");
                if (frame.ContentSize.Width <= 0 || frame.ContentSize.Height <= 0)
                    throw new InvalidOperationException("The game frame had invalid dimensions.");
                using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(
                    CanvasDevice.GetSharedDevice(), frame.Surface, 96);
                using CanvasRenderTarget? cropped = CropClient(window, frame, bitmap);
                using var memory = new MemoryStream();
                await (cropped ?? bitmap).SaveAsync(memory.AsRandomAccessStream(), CanvasBitmapFileFormat.Png);
                completed.TrySetResult(memory.ToArray());
            }
            catch (Exception ex) { completed.TrySetException(ex); }
        };
        session.StartCapture();
        return await completed.Task.ConfigureAwait(false);
    }

    private static CanvasRenderTarget? CropClient(nint window, Direct3D11CaptureFrame frame, CanvasBitmap bitmap)
    {
        // Window Capture includes non-client chrome on some windowed games. Match click_game's client coordinates.
        if (Vanara.PInvoke.User32.GetClientRect(window, out Vanara.PInvoke.RECT rect)
            && rect.Width == frame.ContentSize.Width && rect.Height == frame.ContentSize.Height)
            return null;
        if (!ScreenCaptureService.TryClipClient(window, frame.ContentSize, out Rect clip)
            || clip.Width <= 0 || clip.Height <= 0)
            throw new InvalidOperationException("Cannot map captured window frame to the game client area.");
        var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(),
            (float)clip.Width, (float)clip.Height, 96);
        try
        {
            using CanvasDrawingSession drawing = target.CreateDrawingSession();
            drawing.DrawImage(bitmap, 0, 0, clip);
            return target;
        }
        catch { target.Dispose(); throw; }
    }
}
