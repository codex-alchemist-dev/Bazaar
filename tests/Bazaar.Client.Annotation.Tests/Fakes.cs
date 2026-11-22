using System.Security.Cryptography;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Bazaar.Client.Annotation.Tests;

/// <summary>Records what it was asked to flatten, and returns fixed bytes unless a fault is set.</summary>
internal sealed class FakeFlattener : IAnnotationFlattener
{
    public int Calls { get; private set; }

    public byte[] Output { get; set; } = [1, 2, 3, 4];

    public Exception? Fault { get; set; }

    public Task<FlattenedImage> FlattenAsync(CapturedImage source, AnnotationDocument annotations, CancellationToken cancellationToken)
    {
        Calls++;
        if (Fault is not null) throw Fault;
        return Task.FromResult(new FlattenedImage(Output, 2, 2));
    }
}

/// <summary>Records each upload. Its behaviour is set per test: fail, block until cancelled, or succeed.</summary>
internal sealed class FakeBlobUploader : IBlobUploader
{
    public List<byte[]> Uploads { get; } = [];

    /// <summary>Throws this instead of storing, when set.</summary>
    public Exception? Fault { get; set; }

    /// <summary>When true, the upload waits until its token is cancelled.</summary>
    public bool Block { get; set; }

    /// <summary>Reports the bytes in the order the uploader would, to test the session's progress wiring.</summary>
    public bool ReportProgress { get; set; }

    public int Calls => Uploads.Count;

    public async Task<BlobUploadResult> UploadAsync(BlobUploadRequest request, IProgress<BlobUploadProgress>? progress, CancellationToken cancellationToken)
    {
        Uploads.Add(request.Content.ToArray());
        if (Fault is not null) throw Fault;
        if (Block) await Task.Delay(Timeout.Infinite, cancellationToken);

        var total = request.Content.Length;
        if (ReportProgress)
        {
            progress?.Report(new BlobUploadProgress(total / 2, total));
            progress?.Report(new BlobUploadProgress(total, total));
        }
        return new BlobUploadResult(Convert.ToHexStringLower(SHA256.HashData(request.Content.Span)), total);
    }
}

/// <summary>Helpers that make real PNG images for the headless tests.</summary>
internal static class TestImages
{
    /// <summary>A plain white PNG of the given size, encoded by Avalonia.</summary>
    public static CapturedImage WhitePng(int width, int height)
    {
        using var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            context.FillRectangle(Brushes.White, new Rect(0, 0, width, height));
        }

        using var output = new MemoryStream();
        target.Save(output, new PngBitmapEncoderOptions());
        return new CapturedImage(output.ToArray(), "image/png");
    }
}
