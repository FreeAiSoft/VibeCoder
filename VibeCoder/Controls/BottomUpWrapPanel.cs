using System;
using System.Windows;
using System.Windows.Controls;

namespace VibeCoder.Controls
{
    /// <summary>
    /// Arranges children in columns that fill from bottom to top, wrapping to a new
    /// column on the right when a column is full. Children must be declared in their
    /// visual (top-to-bottom) order: the last child is anchored to the bottom-left and
    /// the first child sits at the top of the left-most column.
    /// </summary>
    /// <remarks>
    /// A plain <see cref="WrapPanel"/> with <c>Orientation="Vertical"</c> fills columns
    /// top-to-bottom, which means the bottom-most child is the first one to overflow into
    /// the next column on the right. The chat input action buttons need the opposite: the
    /// bottom-most button (Send) must stay anchored to the bottom and the top-most button
    /// (Clear) must be the first to move right when the available height shrinks, so this
    /// panel fills columns from the bottom upward instead.
    /// </remarks>
    public sealed class BottomUpWrapPanel : Panel
    {
        /// <inheritdoc />
        protected override Size MeasureOverride(Size availableSize)
        {
            double itemWidth = 0;
            double itemHeight = 0;
            double totalHeight = 0;

            foreach (UIElement child in Children)
            {
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                itemWidth = Math.Max(itemWidth, child.DesiredSize.Width);
                itemHeight = Math.Max(itemHeight, child.DesiredSize.Height);
                totalHeight += child.DesiredSize.Height;
            }

            if (Children.Count == 0 || itemWidth <= 0 || itemHeight <= 0)
                return new Size(0, 0);

            int perColumn = ItemsPerColumn(availableSize.Height, itemHeight, Children.Count);
            int columns = (int)Math.Ceiling((double)Children.Count / perColumn);

            double height = double.IsPositiveInfinity(availableSize.Height)
                ? totalHeight
                : Math.Min(totalHeight, availableSize.Height);

            return new Size(itemWidth * columns, height);
        }

        /// <inheritdoc />
        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Children.Count == 0)
                return finalSize;

            double itemWidth = 0;
            double itemHeight = 0;
            foreach (UIElement child in Children)
            {
                itemWidth = Math.Max(itemWidth, child.DesiredSize.Width);
                itemHeight = Math.Max(itemHeight, child.DesiredSize.Height);
            }

            if (itemWidth <= 0 || itemHeight <= 0)
                return finalSize;

            int perColumn = ItemsPerColumn(finalSize.Height, itemHeight, Children.Count);

            // Iterate backwards so the last child anchors to the bottom of the first
            // column; children that would overflow the top wrap into the next column.
            int column = 0;
            int row = 0;
            for (int i = Children.Count - 1; i >= 0; i--)
            {
                double x = column * itemWidth;
                double y = finalSize.Height - (row + 1) * itemHeight;
                Children[i].Arrange(new Rect(x, y, itemWidth, itemHeight));

                row++;
                if (row >= perColumn)
                {
                    column++;
                    row = 0;
                }
            }

            return finalSize;
        }

        private static int ItemsPerColumn(double availableHeight, double itemHeight, int count)
        {
            if (double.IsPositiveInfinity(availableHeight) || availableHeight <= 0 || itemHeight <= 0)
                return Math.Max(1, count);

            return Math.Max(1, (int)Math.Floor(availableHeight / itemHeight));
        }
    }
}
