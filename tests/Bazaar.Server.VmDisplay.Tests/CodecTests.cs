using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Bazaar.Server.VmDisplay.Abstractions;
using Bazaar.Server.VmDisplay.Codecs;

namespace Bazaar.Server.VmDisplay.Tests;

public class CodecTests
{
    private const int _max = 32 * 1024 * 1024;

    private static DisplayFailure FailureOf(Action act) => Assert.Throws<DisplayException>(act).Failure;

    // Rewrites bytes inside the IHDR chunk and fixes its checksum, so the structure stays valid.
    private static byte[] RewriteIhdr(byte[] png, Action<byte[]> mutate)
    {
        byte[] copy = (byte[])png.Clone();
        mutate(copy);
        uint crc = Crc32.Compute(copy.AsSpan(12, 17));
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(29), crc);
        return copy;
    }

    [Fact]
    public void Crc32_matches_the_standard_check_value() =>
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Png_encode_then_decode_round_trips(int channels)
    {
        PixelBuffer src = Images.Gradient(37, 23, channels);
        PixelBuffer back = PngDecoder.Decode(PngEncoder.Encode(src), _max);
        Assert.Equal(src.Width, back.Width);
        Assert.Equal(src.Height, back.Height);
        Assert.Equal(channels, back.Channels);
        Assert.Equal(src.Data, back.Data);
    }

    [Fact]
    public void Inspector_reports_declared_size_without_decoding()
    {
        PngInfo info = PngInspector.Inspect(Images.Png(40, 30), _max);
        Assert.Equal(40, info.Width);
        Assert.Equal(30, info.Height);
        Assert.Equal(8, info.BitDepth);
        Assert.Equal(2, info.ColorType);
    }

    [Fact]
    public void Decoder_handles_all_five_filter_types()
    {
        const int w = 9;
        const int h = 10;
        PixelBuffer src = Images.Gradient(w, h);
        byte[] png = BuildFilteredPng(src, y => (byte)(y % 5));
        Assert.Equal(src.Data, PngDecoder.Decode(png, _max).Data);
    }

    [Fact]
    public void Decoder_rejects_an_unknown_filter_type()
    {
        PixelBuffer src = Images.Gradient(4, 4);
        byte[] png = BuildFilteredPng(src, _ => 9);
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PngDecoder.Decode(png, _max)));
    }

    [Fact]
    public void Decoder_rejects_palette_16_bit_and_interlaced_images()
    {
        byte[] png = Images.Png(8, 8);
        byte[] palette = RewriteIhdr(png, b => b[25] = 3);
        byte[] deep = RewriteIhdr(png, b => b[24] = 16);
        byte[] interlaced = RewriteIhdr(png, b => b[28] = 1);
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PngDecoder.Decode(palette, _max)));
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PngDecoder.Decode(deep, _max)));
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PngDecoder.Decode(interlaced, _max)));
    }

    [Fact]
    public void Decoder_rejects_image_data_shorter_than_the_header_declares()
    {
        byte[] png = Images.Png(16, 16);
        byte[] taller = RewriteIhdr(png, b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), 40));
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PngDecoder.Decode(taller, _max)));
    }

    [Fact]
    public void Decoder_rejects_image_data_longer_than_the_header_declares()
    {
        byte[] png = Images.Png(16, 16);
        byte[] shorter = RewriteIhdr(png, b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), 4));
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PngDecoder.Decode(shorter, _max)));
    }

    [Fact]
    public void Inspector_rejects_a_truncated_ihdr()
    {
        byte[] png = Images.Png(8, 8);
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PngInspector.Inspect(png.AsSpan(0, 20), _max)));
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PngInspector.Inspect(png.AsSpan(0, 33), _max)));
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PngInspector.Inspect([], _max)));
    }

    [Fact]
    public void Inspector_rejects_huge_dimensions_before_walking_chunks()
    {
        byte[] png = Images.Png(8, 8);
        byte[] huge = (byte[])png.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(16), 100_000);
        BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(20), uint.MaxValue);
        Assert.Equal(DisplayFailure.FrameTooLarge, FailureOf(() => PngInspector.Inspect(huge, _max)));
    }

    [Fact]
    public void Inspector_rejects_zero_dimensions()
    {
        byte[] zero = RewriteIhdr(Images.Png(8, 8), b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), 0));
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PngInspector.Inspect(zero, _max)));
    }

    [Fact]
    public void Inspector_accepts_exactly_the_ceiling_and_rejects_one_more()
    {
        byte[] ok = RewriteIhdr(Images.Png(8, 8), b =>
        {
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), DisplayLimits.CeilingWidth);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), DisplayLimits.CeilingHeight);
        });
        Assert.Equal(DisplayLimits.CeilingWidth, PngInspector.Inspect(ok, _max).Width);
        byte[] over = RewriteIhdr(ok, b => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), DisplayLimits.CeilingWidth + 1));
        Assert.Equal(DisplayFailure.FrameTooLarge, FailureOf(() => PngInspector.Inspect(over, _max)));
    }

    [Fact]
    public void Inspector_rejects_data_over_the_encoded_byte_limit()
    {
        byte[] png = Images.Png(64, 64);
        Assert.Equal(DisplayFailure.FrameTooLarge, FailureOf(() => PngInspector.Inspect(png, png.Length - 1)));
        Assert.Equal(64, PngInspector.Inspect(png, png.Length).Width);
    }

    [Fact]
    public void Inspector_rejects_bad_signature_bad_checksum_and_trailing_bytes()
    {
        byte[] png = Images.Png(8, 8);
        byte[] badSig = (byte[])png.Clone();
        badSig[1] = (byte)'X';
        byte[] badCrc = (byte[])png.Clone();
        badCrc[40] ^= 0xFF;
        byte[] trailing = [.. png, 0];
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PngInspector.Inspect(badSig, _max)));
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PngInspector.Inspect(badCrc, _max)));
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PngInspector.Inspect(trailing, _max)));
    }

    [Fact]
    public void Inspector_rejects_a_chunk_length_that_runs_past_the_data()
    {
        byte[] png = Images.Png(8, 8);
        byte[] bad = (byte[])png.Clone();
        // The IDAT length field follows the 33 byte signature + IHDR prefix.
        BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(33), 0x7FFFFFFF);
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PngInspector.Inspect(bad, _max)));
    }

    [Fact]
    public void Rgb565_converts_known_values_little_endian()
    {
        // 0xF800 red, 0x07E0 green, 0x001F blue, 0xFFFF white.
        byte[] data = [0x00, 0xF8, 0xE0, 0x07, 0x1F, 0x00, 0xFF, 0xFF];
        PixelBuffer p = Rgb565Converter.ToRgb(data, 4, 1);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 }, p.Data);
    }

    [Fact]
    public void Rgb565_frame_survives_a_png_round_trip()
    {
        var raw = new byte[12 * 7 * 2];
        for (int i = 0; i < raw.Length; i++)
            raw[i] = (byte)(i * 7);
        PixelBuffer rgb = Rgb565Converter.ToRgb(raw, 12, 7);
        Assert.Equal(rgb.Data, PngDecoder.Decode(PngEncoder.Encode(rgb), _max).Data);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1)]
    [InlineData(-1)]
    public void Rgb565_rejects_a_wrong_length(int delta)
    {
        var raw = new byte[(4 * 4 * 2) + delta];
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => Rgb565Converter.ToRgb(raw, 4, 4)));
    }

    [Fact]
    public void Rgb565_rejects_declared_dimensions_over_the_ceiling_before_checking_length()
    {
        Assert.Equal(DisplayFailure.FrameTooLarge, FailureOf(() => Rgb565Converter.ToRgb(new byte[4], 100_000, 100_000)));
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => Rgb565Converter.ToRgb(new byte[4], 0, 5)));
    }

    [Fact]
    public void Bgra_swaps_channels_and_drops_alpha()
    {
        byte[] data = [1, 2, 3, 0, 10, 20, 30, 0];
        PixelBuffer p = Bgra32Converter.ToRgb(data, 2, 1);
        Assert.Equal(new byte[] { 3, 2, 1, 30, 20, 10 }, p.Data);
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => Bgra32Converter.ToRgb(data, 2, 2)));
    }

    [Fact]
    public void Ppm_round_trips_through_the_png_codec()
    {
        PixelBuffer src = Images.Gradient(20, 11);
        PixelBuffer parsed = PpmParser.Parse(Images.Ppm(20, 11));
        Assert.Equal(src.Data, parsed.Data);
        Assert.Equal(src.Data, PngDecoder.Decode(PngEncoder.Encode(parsed), _max).Data);
    }

    [Fact]
    public void Ppm_rejects_maxval_65535()
    {
        byte[] ppm = [.. Encoding.ASCII.GetBytes("P6\n2 2\n65535\n"), .. new byte[24]];
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PpmParser.Parse(ppm)));
    }

    [Fact]
    public void Ppm_rejects_header_overflow_and_missing_fields()
    {
        byte[] spaces = [.. "P6"u8, .. Enumerable.Repeat((byte)' ', 500), .. "2 2 255\n"u8, .. new byte[12]];
        byte[] longNumber = [.. "P6\n123456789 2 255\n"u8, .. new byte[12]];
        byte[] noMaxval = "P6\n2 2\n"u8.ToArray();
        byte[] notP6 = "P5\n2 2\n255\n"u8.ToArray();
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PpmParser.Parse(spaces)));
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PpmParser.Parse(longNumber)));
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PpmParser.Parse(noMaxval)));
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PpmParser.Parse(notP6)));
    }

    [Fact]
    public void Ppm_rejects_huge_dimensions_without_allocating_and_wrong_data_length()
    {
        byte[] huge = [.. "P6\n99999 99999\n255\n"u8, .. new byte[30]];
        Assert.Equal(DisplayFailure.FrameTooLarge, FailureOf(() => PpmParser.Parse(huge)));
        byte[] shortData = Images.Ppm(4, 4, new byte[10]);
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PpmParser.Parse(shortData)));
        byte[] extra = [.. Images.Ppm(4, 4), 0];
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => PpmParser.Parse(extra)));
    }

    [Theory]
    [InlineData(4000, 2000, 1920, 960)]
    [InlineData(2000, 4000, 600, 1200)]
    [InlineData(1920, 1200, 1920, 1200)]
    [InlineData(800, 600, 800, 600)]
    [InlineData(3840, 2160, 1920, 1080)]
    public void Fit_size_keeps_the_aspect_ratio_and_never_upscales(int w, int h, int ew, int eh)
    {
        (int rw, int rh) = FrameScaler.FitSize(w, h, 1920, 1200);
        Assert.Equal((ew, eh), (rw, rh));
    }

    [Fact]
    public void Scaler_downscales_and_preserves_a_solid_colour()
    {
        var data = new byte[100 * 50 * 3];
        for (int i = 0; i < data.Length; i += 3)
        {
            data[i] = 200;
            data[i + 1] = 100;
            data[i + 2] = 50;
        }
        PixelBuffer scaled = FrameScaler.FitWithin(new PixelBuffer(100, 50, 3, data), 40, 40);
        Assert.Equal(40, scaled.Width);
        Assert.Equal(20, scaled.Height);
        for (int i = 0; i < scaled.Data.Length; i += 3)
            Assert.Equal(new byte[] { 200, 100, 50 }, scaled.Data[i..(i + 3)]);
    }

    [Fact]
    public void Scaler_returns_the_same_buffer_when_it_already_fits()
    {
        PixelBuffer src = Images.Gradient(10, 10);
        Assert.Same(src, FrameScaler.FitWithin(src, 100, 100));
    }

    [Fact]
    public void Scaler_averages_a_box_of_pixels()
    {
        // 2x1 black and white shrinks to one mid-grey pixel.
        var src = new PixelBuffer(2, 1, 3, [0, 0, 0, 255, 255, 255]);
        PixelBuffer scaled = FrameScaler.FitWithin(src, 1, 1);
        Assert.Equal(new byte[] { 128, 128, 128 }, scaled.Data);
    }

    [Fact]
    public void Normalizer_passes_a_png_within_limits_through_untouched()
    {
        byte[] png = Images.Png(64, 48);
        NormalizedFrame f = FrameNormalizer.FromPng(png, new FrameLimits(1920, 1200, _max));
        Assert.Same(png, f.Png);
        Assert.Equal((64, 48), (f.Width, f.Height));
    }

    [Fact]
    public void Normalizer_passes_through_a_png_the_decoder_could_not_read()
    {
        // 16-bit PNGs are fine when no downscale is needed; only resizing needs the decoder.
        byte[] png = RewriteIhdr(Images.Png(8, 8), b => b[24] = 16);
        NormalizedFrame f = FrameNormalizer.FromPng(png, new FrameLimits(1920, 1200, _max));
        Assert.Same(png, f.Png);
    }

    [Fact]
    public void Normalizer_downscales_an_oversized_png()
    {
        NormalizedFrame f = FrameNormalizer.FromPng(Images.Png(400, 100), new FrameLimits(200, 200, _max));
        Assert.Equal((200, 50), (f.Width, f.Height));
        PngInfo info = PngInspector.Inspect(f.Png, _max);
        Assert.Equal((200, 50), (info.Width, info.Height));
    }

    [Fact]
    public void Normalizer_converts_each_raw_format_to_png()
    {
        var limits = new FrameLimits(1920, 1200, _max);
        NormalizedFrame a = FrameNormalizer.FromRaw(new RawFrame(RawPixelFormat.Rgb565, new byte[8 * 4 * 2], 8, 4), limits);
        NormalizedFrame b = FrameNormalizer.FromRaw(new RawFrame(RawPixelFormat.Bgra32, new byte[8 * 4 * 4], 8, 4), limits);
        NormalizedFrame c = FrameNormalizer.FromRaw(new RawFrame(RawPixelFormat.Ppm, Images.Ppm(8, 4), 0, 0), limits);
        NormalizedFrame d = FrameNormalizer.FromRaw(new RawFrame(RawPixelFormat.Png, Images.Png(8, 4), 0, 0), limits);
        foreach (NormalizedFrame f in new[] { a, b, c, d })
            Assert.Equal((8, 4), (f.Width, f.Height));
    }

    [Fact]
    public void Normalizer_rejects_an_encoded_result_over_the_byte_limit()
    {
        var raw = new RawFrame(RawPixelFormat.Rgb565, new byte[64 * 64 * 2], 64, 64);
        Assert.Equal(DisplayFailure.FrameTooLarge, FailureOf(() => FrameNormalizer.FromRaw(raw, new FrameLimits(1920, 1200, 20))));
    }

    [Fact]
    public void Normalizer_rejects_null_data()
    {
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => FrameNormalizer.FromPng(null!, new FrameLimits(10, 10, _max))));
        Assert.Equal(DisplayFailure.FrameMalformed, FailureOf(() => FrameNormalizer.FromRaw(new RawFrame(RawPixelFormat.Ppm, null!, 0, 0), new FrameLimits(10, 10, _max))));
    }

    // Applies the forward PNG filters so every unfilter branch is exercised.
    private static byte[] BuildFilteredPng(PixelBuffer src, Func<int, byte> filterFor)
    {
        int stride = src.Stride;
        int bpp = src.Channels;
        var raw = new byte[(stride + 1) * src.Height];
        for (int y = 0; y < src.Height; y++)
        {
            byte f = filterFor(y);
            raw[y * (stride + 1)] = f;
            for (int i = 0; i < stride; i++)
            {
                int cur = src.Data[(y * stride) + i];
                int a = i >= bpp ? src.Data[(y * stride) + i - bpp] : 0;
                int b = y > 0 ? src.Data[((y - 1) * stride) + i] : 0;
                int c = i >= bpp && y > 0 ? src.Data[((y - 1) * stride) + i - bpp] : 0;
                int pred = f switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => PaethPredictor(a, b, c),
                    _ => 0,
                };
                raw[(y * (stride + 1)) + 1 + i] = (byte)(cur - pred);
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, true))
            z.Write(raw);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)src.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)src.Height);
        ihdr[8] = 8;
        ihdr[9] = src.Channels == 4 ? (byte)6 : (byte)2;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : (pb <= pc ? b : c);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
        s.Write(len);
        byte[] body = [.. Encoding.ASCII.GetBytes(type), .. data];
        s.Write(body);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32.Compute(body));
        s.Write(crc);
    }
}
