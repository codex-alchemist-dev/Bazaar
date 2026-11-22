using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Bazaar.Client.Motion.Avalonia;

/// <summary>Turns a <see cref="MotionFrame"/> into transform and opacity on a control: nothing else is ever animated.</summary>
public static class MotionTransforms
{
    /// <summary>True when the frame has no geometry, so the control carries no transform at all.</summary>
    public static bool IsIdentityGeometry(in MotionFrame f) =>
        f.Tx == 0 && f.Ty == 0 && f.Scale == 1 && f.Along == 1 && f.Across == 1;

    /// <summary>The matrix about the control's centre: rotate to the axis, scale along and across, rotate back, scale, translate.</summary>
    public static Matrix ToMatrix(in MotionFrame f)
    {
        var axis = f.AxisDegrees * Math.PI / 180.0;
        return Matrix.CreateRotation(-axis)
            * Matrix.CreateScale(f.Along, f.Across)
            * Matrix.CreateRotation(axis)
            * Matrix.CreateScale(f.Scale, f.Scale)
            * Matrix.CreateTranslation(f.Tx, f.Ty);
    }

    /// <summary>Applies the frame to <paramref name="target"/>; with <paramref name="content"/> set, its opacity follows <see cref="MotionFrame.ContentOpacity"/>.</summary>
    public static void Apply(Control target, Control? content, in MotionFrame frame)
    {
        if (IsIdentityGeometry(frame))
        {
            target.RenderTransform = null;
        }
        else
        {
            target.RenderTransformOrigin = RelativePoint.Center;
            target.RenderTransform = new MatrixTransform(ToMatrix(frame));
        }

        target.Opacity = frame.Opacity;
        if (content is not null)
        {
            content.Opacity = frame.ContentOpacity;
        }
    }
}
