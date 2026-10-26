using System;
using System.Windows;
using System.Windows.Controls;

namespace Squish.App.Controls;

/// <summary>
/// Responsive panel that lays out children in equal-width cards, automatically
/// adjusting column count based on the available width.
/// </summary>
public class SmartWrapPanel : System.Windows.Controls.Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth),
        typeof(double),
        typeof(SmartWrapPanel),
        new FrameworkPropertyMetadata(320d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing),
        typeof(double),
        typeof(SmartWrapPanel),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing),
        typeof(double),
        typeof(SmartWrapPanel),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding),
        typeof(Thickness),
        typeof(SmartWrapPanel),
        new FrameworkPropertyMetadata(new Thickness(0), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        var padding = Padding;
        double horizontalPadding = padding.Left + padding.Right;
        double verticalPadding = padding.Top + padding.Bottom;

        if (InternalChildren.Count == 0)
        {
            return new System.Windows.Size(horizontalPadding, verticalPadding);
        }

        double width = double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
            ? (MinColumnWidth * InternalChildren.Count) + (HorizontalSpacing * Math.Max(0, InternalChildren.Count - 1))
            : availableSize.Width;
        width = Math.Max(0, width - horizontalPadding);

        int columns = CalculateColumnCount(width);
        double childWidth = CalculateChildWidth(width, columns);

        double totalHeight = 0;
        double lineHeight = 0;
        int colIndex = 0;

        foreach (UIElement child in InternalChildren)
        {
            if (child == null)
            {
                continue;
            }

            child.Measure(new System.Windows.Size(childWidth, double.PositiveInfinity));
            lineHeight = Math.Max(lineHeight, child.DesiredSize.Height);

            colIndex++;
            if (colIndex == columns)
            {
                totalHeight += lineHeight + VerticalSpacing;
                lineHeight = 0;
                colIndex = 0;
            }
        }

        if (colIndex > 0)
        {
            totalHeight += lineHeight;
        }
        else if (totalHeight > 0)
        {
            totalHeight -= VerticalSpacing; // remove trailing spacing
        }

        totalHeight += verticalPadding;
        width += horizontalPadding;

        return new System.Windows.Size(width, totalHeight);
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
    {
        var padding = Padding;
        double horizontalPadding = padding.Left + padding.Right;
        double verticalPadding = padding.Top + padding.Bottom;

        double width = Math.Max(0, finalSize.Width - horizontalPadding);
        int columns = CalculateColumnCount(width);
        double childWidth = CalculateChildWidth(width, columns);

        // Every child in a row is stretched to the tallest one so cards line up.
        var rowHeights = new System.Collections.Generic.List<double>();
        int index = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child == null)
            {
                continue;
            }

            if (index % columns == 0)
            {
                rowHeights.Add(0);
            }

            rowHeights[^1] = Math.Max(rowHeights[^1], child.DesiredSize.Height);
            index++;
        }

        double x = padding.Left;
        double y = padding.Top;
        double totalHeight = padding.Top;
        double lineHeight = 0;
        int colIndex = 0;
        int rowIndex = 0;

        foreach (UIElement child in InternalChildren)
        {
            if (child == null)
            {
                continue;
            }

            double childHeight = rowHeights[rowIndex];
            Rect rect = new(x, y, childWidth, childHeight);
            child.Arrange(rect);

            lineHeight = Math.Max(lineHeight, childHeight);
            colIndex++;

            if (colIndex == columns)
            {
                x = padding.Left;
                y += lineHeight + VerticalSpacing;
                totalHeight = y;
                lineHeight = 0;
                colIndex = 0;
                rowIndex++;
            }
            else
            {
                x += childWidth + HorizontalSpacing;
            }
        }

        double contentHeight = totalHeight;
        if (colIndex > 0)
        {
            contentHeight += lineHeight;
        }
        else if (contentHeight > padding.Top)
        {
            contentHeight -= VerticalSpacing;
        }

        double usedHeight = Math.Max(contentHeight + padding.Bottom, finalSize.Height);
        double usedWidth = Math.Max(width + horizontalPadding, finalSize.Width);

        return new System.Windows.Size(usedWidth, usedHeight);
    }

    private int CalculateColumnCount(double availableWidth)
    {
        var totalWidth = availableWidth;
        double requiredWidth = MinColumnWidth + HorizontalSpacing;
        int columns = (int)Math.Floor((totalWidth + HorizontalSpacing) / requiredWidth);
        return Math.Max(1, columns);
    }

    private double CalculateChildWidth(double availableWidth, int columns)
    {
        if (columns <= 1)
        {
            return availableWidth;
        }

        double totalSpacing = HorizontalSpacing * (columns - 1);
        double width = (availableWidth - totalSpacing) / columns;
        return Math.Max(0, width);
    }
}
