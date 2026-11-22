namespace Bazaar.Client.Annotation.Tests;

/// <summary>The annotation model: what it accepts, how it clamps, and undo.</summary>
public class AnnotationModelTests
{
    private static readonly NormPoint _a = new(0.1, 0.1);
    private static readonly NormPoint _b = new(0.9, 0.9);

    [Fact]
    public void Stroke_is_clamped_and_kept_in_drawing_order()
    {
        var doc = new AnnotationDocument(new AnnotationOptions());
        var points = new[] { new NormPoint(-1, 0.5), new NormPoint(0.5, 2) };

        Assert.True(doc.TryAddStroke(points));

        var stroke = Assert.IsType<StrokeItem>(Assert.Single(doc.Items));
        Assert.Equal(new NormPoint(0, 0.5), stroke.Points[0]);
        Assert.Equal(new NormPoint(0.5, 1), stroke.Points[1]);
        Assert.Equal(new AnnotationOptions().InkArgb, stroke.Argb);
    }

    [Fact]
    public void Stroke_with_fewer_than_two_points_is_refused()
    {
        var doc = new AnnotationDocument(new AnnotationOptions());

        Assert.False(doc.TryAddStroke([_a]));
        Assert.False(doc.TryAddStroke([]));
        Assert.Equal(0, doc.Count);
    }

    [Fact]
    public void Stroke_over_the_point_limit_is_refused()
    {
        var doc = new AnnotationDocument(new AnnotationOptions(MaxPointsPerStroke: 3));

        Assert.False(doc.TryAddStroke([_a, _b, _a, _b]));
        Assert.True(doc.TryAddStroke([_a, _b, _a]));
    }

    [Fact]
    public void Items_stop_at_the_item_limit_and_are_not_truncated()
    {
        var doc = new AnnotationDocument(new AnnotationOptions(MaxItems: 2));

        Assert.True(doc.TryAddStroke([_a, _b]));
        Assert.True(doc.TryAddText(_a, "one"));
        Assert.False(doc.TryAddText(_b, "two"));
        Assert.False(doc.TryAddStroke([_a, _b]));
        Assert.Equal(2, doc.Count);
    }

    [Fact]
    public void Text_is_trimmed_and_empty_or_long_text_is_refused()
    {
        var doc = new AnnotationDocument(new AnnotationOptions(MaxTextLength: 5));

        Assert.False(doc.TryAddText(_a, "   "));
        Assert.False(doc.TryAddText(_a, "123456"));
        Assert.True(doc.TryAddText(_a, "  hi  "));

        var text = Assert.IsType<TextItem>(Assert.Single(doc.Items));
        Assert.Equal("hi", text.Text);
    }

    [Fact]
    public void Undo_removes_the_last_item_and_reports_when_empty()
    {
        var doc = new AnnotationDocument(new AnnotationOptions());
        doc.TryAddText(_a, "first");
        doc.TryAddText(_b, "second");

        Assert.True(doc.Undo());
        Assert.Equal("first", Assert.IsType<TextItem>(Assert.Single(doc.Items)).Text);
        Assert.True(doc.Undo());
        Assert.False(doc.Undo());
    }
}

/// <summary>The client limits: defaults are usable and out-of-range values are named.</summary>
public class AnnotationOptionsTests
{
    [Fact]
    public void Defaults_validate_cleanly_and_stay_within_the_server_ceilings()
    {
        var options = new AnnotationOptions();

        Assert.Empty(options.Validate());
        Assert.True(options.MaxCaptureBytes <= 26_214_400, "client cap must not exceed gc.blob_max_bytes default");
        Assert.True(options.ChunkBytes <= 4 * 1024 * 1024, "chunk must not exceed the server chunk ceiling");
    }

    [Fact]
    public void Out_of_range_values_are_listed()
    {
        var options = new AnnotationOptions(ChunkBytes: 5 * 1024 * 1024, MaxAttemptsPerRequest: 0, StrokeWidthFraction: 0.5);

        var errors = options.Validate();

        Assert.Contains(errors, e => e.StartsWith("ChunkBytes", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("MaxAttemptsPerRequest", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("StrokeWidthFraction", StringComparison.Ordinal));
    }
}
