using System.Buffers.Binary;

namespace Bazaar.Diagnostics.Ingestion;

/// <summary>The two screenshot formats the server accepts (doc 43, section 12.1 step 4).</summary>
public enum ImageFormat
{
    /// <summary>Portable Network Graphics, content type <c>image/png</c>.</summary>
    Png,

    /// <summary>JPEG (JFIF or plain), content type <c>image/jpeg</c>.</summary>
    Jpeg,
}

/// <summary>The result of reading an image header: the size and format, or the refusal code that says why not.</summary>
/// <param name="Width">Width in pixels. Zero when <see cref="Refusal"/> is set.</param>
/// <param name="Height">Height in pixels. Zero when <see cref="Refusal"/> is set.</param>
/// <param name="Refusal">The reason the image was not accepted, or null.</param>
public readonly record struct ImageHeaderResult(int Width, int Height, IngestionRefusalCode? Refusal)
{
    /// <summary>An accepted header.</summary>
    public static ImageHeaderResult Accept(int width, int height) => new(width, height, null);

    /// <summary>A refused header.</summary>
    public static ImageHeaderResult Refuse(IngestionRefusalCode code) => new(0, 0, code);
}

/// <summary>
/// Reads the magic bytes and the header of a PNG or JPEG screenshot. Only the bytes that carry the size and the metadata
/// check are read: PNG chunk headers up to the end chunk (chunk data is skipped by length), and JPEG segments up to the
/// start of scan. No third-party library and no pixel decoding. Every read is bounds-checked; malformed input refuses.
/// </summary>
public static class ImageHeaders
{
    private const int _pngChunkOverhead = 12;
    private const int _jpegSoiLength = 2;

    private static readonly byte[] _pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The format a declared content type names, or null when the type is not one of the two accepted.</summary>
    public static ImageFormat? FormatOf(string contentType)
    {
        if (string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase))
        {
            return ImageFormat.Png;
        }

        if (string.Equals(contentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return ImageFormat.Jpeg;
        }

        return null;
    }

    /// <summary>Detects the format from the leading magic bytes, or returns null when they match neither format.</summary>
    public static ImageFormat? DetectFormat(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(_pngSignature))
        {
            return ImageFormat.Png;
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }

        return null;
    }

    /// <summary>Reads the header of an image already known to be <paramref name="format"/>.</summary>
    public static ImageHeaderResult Read(ReadOnlySpan<byte> data, ImageFormat format) => format switch
    {
        ImageFormat.Png => ReadPng(data),
        ImageFormat.Jpeg => ReadJpeg(data),
        _ => ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMagicMismatch),
    };

    private static ImageHeaderResult ReadPng(ReadOnlySpan<byte> data)
    {
        if (!data.StartsWith(_pngSignature))
        {
            return ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMagicMismatch);
        }

        // Walk the chunk headers. Only IHDR, PLTE, tRNS and IDAT may appear; anything else is metadata, and the walk
        // must end exactly at IEND with no bytes after it.
        var offset = _pngSignature.Length;
        var sawHeader = false;
        var sawData = false;
        var width = 0;
        var height = 0;

        while (true)
        {
            if (data.Length - offset < _pngChunkOverhead)
            {
                return ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMalformed);
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            if (length > (uint)(data.Length - offset - _pngChunkOverhead))
            {
                return ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMalformed);
            }

            var type = data.Slice(offset + 4, 4);
            var body = data.Slice(offset + 8, (int)length);

            if (!sawHeader)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13)
                {
                    return ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMalformed);
                }

                var pngWidth = BinaryPrimitives.ReadUInt32BigEndian(body[..4]);
                var pngHeight = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4));
                if (pngWidth == 0 || pngHeight == 0)
                {
                    return ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMalformed);
                }

                if (pngWidth > IngestionLimits.MaxImageDimension || pngHeight > IngestionLimits.MaxImageDimension)
                {
                    return ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentDimensionsExceeded);
                }

                width = (int)pngWidth;
                height = (int)pngHeight;
                sawHeader = true;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                sawData = true;
            }
            else if (type.SequenceEqual("PLTE"u8) || type.SequenceEqual("tRNS"u8))
            {
                // Palette and transparency chunks change how pixels are read, so they are allowed.
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (!sawData)
                {
                    return ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMalformed);
                }

                return offset + _pngChunkOverhead + (int)length == data.Length
                    ? ImageHeaderResult.Accept(width, height)
                    : ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMetadataPresent);
            }
            else
            {
                return ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMetadataPresent);
            }

            offset += _pngChunkOverhead + (int)length;
        }
    }

    private static ImageHeaderResult ReadJpeg(ReadOnlySpan<byte> data)
    {
        // A complete JPEG ends with EOI. Anything after it, or a missing EOI, is refused.
        if (data.Length < _jpegSoiLength + 2 || data[^2] != 0xFF || data[^1] != 0xD9)
        {
            return ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMalformed);
        }

        // Segments are FF, a marker, and (except fill bytes) a two-byte big-endian length that counts itself.
        // Stop at SOS, the start of scan: the pixels follow and are not read.
        var offset = _jpegSoiLength;
        var sawFrame = false;
        var width = 0;
        var height = 0;

        while (true)
        {
            if (data.Length - offset < 4 || data[offset] != 0xFF)
            {
                return ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMalformed);
            }

            var marker = data[offset + 1];
            if (marker == 0xFF)
            {
                offset++;
                continue;
            }

            var length = (data[offset + 2] << 8) | data[offset + 3];
            if (length < 2 || offset + 2 + length > data.Length)
            {
                return ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMalformed);
            }

            var payload = data.Slice(offset + 4, length - 2);

            if (marker == 0xDA)
            {
                return sawFrame
                    ? ImageHeaderResult.Accept(width, height)
                    : ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMalformed);
            }

            if (IsStartOfFrame(marker))
            {
                // Baseline (C0) and progressive (C2) only; other frame types are not accepted.
                if (sawFrame || (marker != 0xC0 && marker != 0xC2) || payload.Length < 6)
                {
                    return ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMalformed);
                }

                var jpegHeight = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(1, 2));
                var jpegWidth = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(3, 2));
                if (jpegWidth == 0 || jpegHeight == 0)
                {
                    return ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMalformed);
                }

                if (jpegWidth > IngestionLimits.MaxImageDimension || jpegHeight > IngestionLimits.MaxImageDimension)
                {
                    return ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentDimensionsExceeded);
                }

                width = jpegWidth;
                height = jpegHeight;
                sawFrame = true;
            }
            else if (marker is 0xC4 or 0xDB or 0xDD)
            {
                // Huffman tables, quantisation tables and restart interval: needed to decode, so allowed.
            }
            else if (marker == 0xE0 && payload.StartsWith("JFIF\0"u8))
            {
                // The JFIF header is the only application segment allowed.
            }
            else if (marker is >= 0xE0 and <= 0xEF or 0xFE)
            {
                // Other APPn segments (EXIF, XMP, ICC) and comments are metadata.
                return ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMetadataPresent);
            }
            else
            {
                return ImageHeaderResult.Refuse(IngestionRefusalCode.AttachmentMalformed);
            }

            offset += 2 + length;
        }
    }

    private static bool IsStartOfFrame(byte marker) =>
        marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
}
