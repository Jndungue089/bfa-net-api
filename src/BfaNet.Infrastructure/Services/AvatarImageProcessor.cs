using BfaNet.Application.Common;
using SkiaSharp;

namespace BfaNet.Infrastructure.Services;

/// <summary>
/// Turns whatever the user uploaded into a small, clean, square JPEG. Doing this on the server means the
/// client only ships the original bytes, and what is stored is always re-encoded pixels: EXIF (GPS, device),
/// ICC profiles, animation frames and any payload hidden in the container are dropped by construction.
/// </summary>
public static class AvatarImageProcessor
{
    public const int Size = 512;
    private const int MaxSide = 8000;          // guards against decompression bombs:
    private const long MaxPixels = 30_000_000; // the header is checked before any pixel is decoded
    private static readonly int[] Qualities = [82, 70, 55, 40, 30];

    public static byte[] ToAvatarJpeg(byte[] original, int maxBytes)
    {
        var invalid = AppException.Invalid("file", "Não foi possível ler a imagem. Escolha outra fotografia.");

        using var stream = new MemoryStream(original, writable: false);
        using var codec = SKCodec.Create(stream) ?? throw invalid;
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || info.Width > MaxSide || info.Height > MaxSide || (long)info.Width * info.Height > MaxPixels)
            throw AppException.Invalid("file", "A imagem tem dimensões demasiado grandes.");

        using var decoded = SKBitmap.Decode(codec) ?? throw invalid;
        using var oriented = ApplyOrientation(decoded, codec.EncodedOrigin);

        var side = Math.Min(oriented.Width, oriented.Height);
        var src = SKRect.Create((oriented.Width - side) / 2f, (oriented.Height - side) / 2f, side, side);

        using var surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Opaque)) ?? throw invalid;
        surface.Canvas.Clear(SKColors.White); // flatten transparency (PNG/WebP) onto white
        using var image = SKImage.FromBitmap(oriented);
        surface.Canvas.DrawImage(image, src, new SKRect(0, 0, Size, Size), new SKSamplingOptions(SKCubicResampler.Mitchell), null);

        using var snapshot = surface.Snapshot();
        foreach (var quality in Qualities)
        {
            using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, quality);
            if (data is not null && data.Size <= maxBytes) return data.ToArray();
        }
        throw AppException.Invalid("file", "Não foi possível comprimir a imagem.");
    }

    /// <summary>Bakes the EXIF rotation into the pixels (the stored file carries no EXIF).</summary>
    private static SKBitmap ApplyOrientation(SKBitmap bmp, SKEncodedOrigin origin)
    {
        var (w, h) = (bmp.Width, bmp.Height);
        var swap = origin is SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftBottom or SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightBottom;
        if (origin == SKEncodedOrigin.TopLeft) return bmp.Copy();

        var result = new SKBitmap(swap ? h : w, swap ? w : h, bmp.ColorType, bmp.AlphaType);
        using var canvas = new SKCanvas(result);
        switch (origin)
        {
            case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(h, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, w); canvas.RotateDegrees(270); break;
            case SKEncodedOrigin.TopRight: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, h); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(h, w); canvas.RotateDegrees(270); canvas.Scale(1, -1); break;
        }
        canvas.DrawBitmap(bmp, 0, 0);
        return result;
    }
}
