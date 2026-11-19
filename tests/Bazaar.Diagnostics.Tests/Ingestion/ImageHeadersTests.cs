using System.Buffers.Binary;
using Bazaar.Diagnostics.Ingestion;

namespace Bazaar.Diagnostics.Tests.Ingestion;

public class ImageHeadersTests
{
    private static readonly byte[] _pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] Chunk(string type, byte[] body)
    {
        var bytes = new byte[12 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), (uint)body.Length);
        System.Text.Encoding.ASCII.GetBytes(type, bytes.AsSpan(4, 4));
        body.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] Header(uint width, uint height)
    {
        var body = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4, 4), height);
        body[8] = 8;
        body[9] = 2;
        return Chunk("IHDR", body);
    }

    private static byte[] Png(uint width, uint height, params byte[][] extraChunksBeforeData) =>
        [.. _pngSignature, .. Header(width, height), .. extraChunksBeforeData.SelectMany(chunk => chunk), .. Chunk("IDAT", [1, 2, 3]), .. Chunk("IEND", [])];

    private static byte[] Segment(byte marker, byte[] payload) =>
        [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)((payload.Length + 2) & 0xFF), .. payload];

    private static byte[] Frame(ushort width, ushort height, byte marker = 0xC0) =>
        Segment(marker, [8, (byte)(height >> 8), (byte)(height & 0xFF), (byte)(width >> 8), (byte)(width & 0xFF), 1, 1, 0x11, 0]);

    private static byte[] Jpeg(params byte[][] segments) =>
        [0xFF, 0xD8, .. segments.SelectMany(segment => segment), .. Segment(0xDA, [1, 1, 0, 0, 0x3F, 0]), 0x00, 0xFF, 0xD9];

    [Fact]
    public void Content_types_map_to_formats_case_insensitively()
    {
        Assert.Equal(ImageFormat.Png, ImageHeaders.FormatOf("IMAGE/PNG"));
        Assert.Equal(ImageFormat.Jpeg, ImageHeaders.FormatOf("image/jpeg"));
        Assert.Null(ImageHeaders.FormatOf("image/gif"));
    }

    [Fact]
    public void Magic_bytes_detect_the_format()
    {
        Assert.Equal(ImageFormat.Png, ImageHeaders.DetectFormat(Png(4, 3)));
        Assert.Equal(ImageFormat.Jpeg, ImageHeaders.DetectFormat(Jpeg(Frame(4, 3))));
        Assert.Null(ImageHeaders.DetectFormat("GIF89a"u8));
        Assert.Null(ImageHeaders.DetectFormat([]));
    }

    [Fact]
    public void Valid_png_is_accepted_with_its_size()
    {
        var result = ImageHeaders.Read(Png(640, 480), ImageFormat.Png);

        Assert.Null(result.Refusal);
        Assert.Equal((640, 480), (result.Width, result.Height));
    }

    [Fact]
    public void Png_with_palette_and_transparency_chunks_is_accepted()
    {
        var result = ImageHeaders.Read(Png(2, 2, Chunk("PLTE", [0, 0, 0]), Chunk("tRNS", [0])), ImageFormat.Png);

        Assert.Null(result.Refusal);
    }

    [Theory]
    [InlineData("tEXt")]
    [InlineData("eXIf")]
    [InlineData("iTXt")]
    [InlineData("iCCP")]
    public void Png_metadata_chunks_are_refused(string type)
    {
        var result = ImageHeaders.Read(Png(2, 2, Chunk(type, [1, 2])), ImageFormat.Png);

        Assert.Equal(IngestionRefusalCode.AttachmentMetadataPresent, result.Refusal);
    }

    [Fact]
    public void Png_over_the_dimension_cap_is_refused()
    {
        var result = ImageHeaders.Read(Png(IngestionLimits.MaxImageDimension + 1, 10), ImageFormat.Png);

        Assert.Equal(IngestionRefusalCode.AttachmentDimensionsExceeded, result.Refusal);
    }

    [Fact]
    public void Png_with_a_zero_dimension_is_malformed()
    {
        Assert.Equal(IngestionRefusalCode.AttachmentMalformed, ImageHeaders.Read(Png(0, 10), ImageFormat.Png).Refusal);
    }

    [Fact]
    public void Png_with_trailing_bytes_after_the_end_chunk_is_refused()
    {
        byte[] data = [.. Png(2, 2), 0x00];

        Assert.Equal(IngestionRefusalCode.AttachmentMetadataPresent, ImageHeaders.Read(data, ImageFormat.Png).Refusal);
    }

    [Fact]
    public void Truncated_png_is_malformed_at_every_length()
    {
        var full = Png(2, 2);

        for (var length = 0; length < full.Length; length++)
        {
            var result = ImageHeaders.Read(full.AsSpan(0, length), ImageFormat.Png);

            Assert.NotNull(result.Refusal);
        }
    }

    [Fact]
    public void Png_chunk_length_larger_than_the_data_is_malformed()
    {
        var data = Png(2, 2);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), uint.MaxValue);

        Assert.Equal(IngestionRefusalCode.AttachmentMalformed, ImageHeaders.Read(data, ImageFormat.Png).Refusal);
    }

    [Fact]
    public void Valid_jpeg_is_accepted_with_its_size()
    {
        var result = ImageHeaders.Read(Jpeg(Segment(0xE0, [.. "JFIF\0"u8.ToArray(), 1, 1, 0, 0, 1, 0, 1, 0, 0]), Frame(800, 600)), ImageFormat.Jpeg);

        Assert.Null(result.Refusal);
        Assert.Equal((800, 600), (result.Width, result.Height));
    }

    [Theory]
    [InlineData(0xE1)]
    [InlineData(0xE2)]
    [InlineData(0xFE)]
    public void Jpeg_exif_icc_and_comment_segments_are_refused(int marker)
    {
        var result = ImageHeaders.Read(Jpeg(Segment((byte)marker, [1, 2, 3]), Frame(10, 10)), ImageFormat.Jpeg);

        Assert.Equal(IngestionRefusalCode.AttachmentMetadataPresent, result.Refusal);
    }

    [Fact]
    public void Jpeg_over_the_dimension_cap_is_refused()
    {
        var result = ImageHeaders.Read(Jpeg(Frame(IngestionLimits.MaxImageDimension + 1, 10)), ImageFormat.Jpeg);

        Assert.Equal(IngestionRefusalCode.AttachmentDimensionsExceeded, result.Refusal);
    }

    [Fact]
    public void Jpeg_without_end_marker_or_frame_is_malformed()
    {
        var noEnd = Jpeg(Frame(10, 10)).AsSpan(0, Jpeg(Frame(10, 10)).Length - 2).ToArray();
        var noFrame = Jpeg();

        Assert.Equal(IngestionRefusalCode.AttachmentMalformed, ImageHeaders.Read(noEnd, ImageFormat.Jpeg).Refusal);
        Assert.Equal(IngestionRefusalCode.AttachmentMalformed, ImageHeaders.Read(noFrame, ImageFormat.Jpeg).Refusal);
    }

    [Fact]
    public void Jpeg_with_an_unsupported_frame_type_is_malformed()
    {
        var result = ImageHeaders.Read(Jpeg(Frame(10, 10, marker: 0xC1)), ImageFormat.Jpeg);

        Assert.Equal(IngestionRefusalCode.AttachmentMalformed, result.Refusal);
    }

    [Fact]
    public void Random_bytes_never_throw()
    {
        var random = new Random(4242);
        for (var round = 0; round < 500; round++)
        {
            var data = new byte[random.Next(0, 64)];
            random.NextBytes(data);
            if (round % 2 == 0 && data.Length >= 8)
            {
                _pngSignature.CopyTo(data, 0);
            }

            _ = ImageHeaders.Read(data, ImageFormat.Png);
            _ = ImageHeaders.Read(data, ImageFormat.Jpeg);
        }
    }
}
