using System;

namespace VibeCoder.Core.Services
{
    /// <summary>
    /// Pure color math used by the theme settings (no WPF dependency).
    /// Kept in Core so it can be unit-tested independently of the UI layer.
    /// </summary>
    public static class ThemeColorMath
    {
        /// <summary>Converts an HSV color to RGB. <paramref name="hue"/> is in degrees and wraps into [0,360).</summary>
        public static void HsvToRgb(double hue, double saturation, double value, out byte r, out byte g, out byte b)
        {
            hue = NormalizeHue(hue);
            saturation = Clamp01(saturation);
            value = Clamp01(value);

            double c = value * saturation;
            double x = c * (1.0 - Math.Abs((hue / 60.0) % 2.0 - 1.0));
            double m = value - c;

            double rp, gp, bp;
            if (hue < 60) { rp = c; gp = x; bp = 0; }
            else if (hue < 120) { rp = x; gp = c; bp = 0; }
            else if (hue < 180) { rp = 0; gp = c; bp = x; }
            else if (hue < 240) { rp = 0; gp = x; bp = c; }
            else if (hue < 300) { rp = x; gp = 0; bp = c; }
            else { rp = c; gp = 0; bp = x; }

            r = ToByte((rp + m) * 255.0);
            g = ToByte((gp + m) * 255.0);
            b = ToByte((bp + m) * 255.0);
        }

        /// <summary>Formats an RGB color as an uppercase hex string, e.g. "#FF0000".</summary>
        public static string RgbToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

        /// <summary>
        /// Parses a hex color string. Accepts "#RGB", "#RRGGBB", "#AARRGGBB" and the same without the leading '#'.
        /// The alpha channel (when present) is ignored.
        /// </summary>
        public static bool TryParseHex(string hex, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            if (string.IsNullOrWhiteSpace(hex)) return false;

            string s = hex.Trim().TrimStart('#');
            if (s.Length == 3)
            {
                return TryParseByte(new string(s[0], 2), out r)
                    && TryParseByte(new string(s[1], 2), out g)
                    && TryParseByte(new string(s[2], 2), out b);
            }

            if (s.Length == 6)
            {
                return TryParseByte(s.Substring(0, 2), out r)
                    && TryParseByte(s.Substring(2, 2), out g)
                    && TryParseByte(s.Substring(4, 2), out b);
            }

            if (s.Length == 8)
            {
                // #AARRGGBB — skip the alpha channel.
                return TryParseByte(s.Substring(2, 2), out r)
                    && TryParseByte(s.Substring(4, 2), out g)
                    && TryParseByte(s.Substring(6, 2), out b);
            }

            return false;
        }

        /// <summary>Converts a hue (degrees) to a seed color hex using the default saturation/value used by the theme slider.</summary>
        public static string HueToSeedHex(double hue, double saturation = 0.5, double value = 1.0)
        {
            HsvToRgb(hue, saturation, value, out byte r, out byte g, out byte b);
            return RgbToHex(r, g, b);
        }

        private static double NormalizeHue(double hue)
        {
            double result = hue % 360.0;
            return result < 0 ? result + 360.0 : result;
        }

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value) || value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }

        private static byte ToByte(double value)
        {
            if (value <= 0) return 0;
            if (value >= 255) return 255;
            return (byte)Math.Round(value);
        }

        private static bool TryParseByte(string hexPair, out byte value)
        {
            int hi = HexValue(hexPair[0]);
            int lo = HexValue(hexPair[1]);
            if (hi < 0 || lo < 0)
            {
                value = 0;
                return false;
            }
            value = (byte)((hi << 4) | lo);
            return true;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
