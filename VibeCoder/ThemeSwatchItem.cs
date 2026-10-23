using System.Windows.Media;

namespace VibeCoder
{
    /// <summary>A single preset Material Design color swatch shown in the theme settings.</summary>
    internal sealed class ThemeSwatchItem
    {
        public string Name { get; set; } = "";
        public Color Primary { get; set; }
        public Color? Secondary { get; set; }
        public Brush DisplayBrush { get; set; } = Brushes.Transparent;
    }
}
