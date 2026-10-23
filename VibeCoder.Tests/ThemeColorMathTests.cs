using NUnit.Framework;
using VibeCoder.Core.Services;

namespace VibeCoder.Tests
{
    [TestFixture]
    public class ThemeColorMathTests
    {
        [Test]
        public void HsvToRgb_PrimaryHues_ReturnsExpectedColors()
        {
            ThemeColorMath.HsvToRgb(0, 1.0, 1.0, out byte r1, out byte g1, out byte b1);
            Assert.That((r1, g1, b1), Is.EqualTo((255, 0, 0)));

            ThemeColorMath.HsvToRgb(60, 1.0, 1.0, out byte r2, out byte g2, out byte b2);
            Assert.That((r2, g2, b2), Is.EqualTo((255, 255, 0)));

            ThemeColorMath.HsvToRgb(120, 1.0, 1.0, out byte r3, out byte g3, out byte b3);
            Assert.That((r3, g3, b3), Is.EqualTo((0, 255, 0)));

            ThemeColorMath.HsvToRgb(180, 1.0, 1.0, out byte r4, out byte g4, out byte b4);
            Assert.That((r4, g4, b4), Is.EqualTo((0, 255, 255)));

            ThemeColorMath.HsvToRgb(240, 1.0, 1.0, out byte r5, out byte g5, out byte b5);
            Assert.That((r5, g5, b5), Is.EqualTo((0, 0, 255)));

            ThemeColorMath.HsvToRgb(300, 1.0, 1.0, out byte r6, out byte g6, out byte b6);
            Assert.That((r6, g6, b6), Is.EqualTo((255, 0, 255)));
        }

        [Test]
        public void HsvToRgb_HueWrapsAround360()
        {
            ThemeColorMath.HsvToRgb(360, 1.0, 1.0, out byte r1, out byte g1, out byte b1);
            Assert.That((r1, g1, b1), Is.EqualTo((255, 0, 0)));

            ThemeColorMath.HsvToRgb(-120, 1.0, 1.0, out byte r2, out byte g2, out byte b2);
            Assert.That((r2, g2, b2), Is.EqualTo((0, 0, 255)));
        }

        [Test]
        public void HsvToRgb_ZeroValue_ReturnsBlack()
        {
            ThemeColorMath.HsvToRgb(123, 0.5, 0.0, out byte r, out byte g, out byte b);
            Assert.That((r, g, b), Is.EqualTo((0, 0, 0)));
        }

        [Test]
        public void HsvToRgb_ZeroSaturation_ReturnsGrey()
        {
            ThemeColorMath.HsvToRgb(0, 0.0, 1.0, out byte r1, out byte g1, out byte b1);
            Assert.That((r1, g1, b1), Is.EqualTo((255, 255, 255)));

            ThemeColorMath.HsvToRgb(0, 0.0, 0.0, out byte r2, out byte g2, out byte b2);
            Assert.That((r2, g2, b2), Is.EqualTo((0, 0, 0)));
        }

        [Test]
        public void RgbToHex_FormatsUppercase()
        {
            Assert.That(ThemeColorMath.RgbToHex(255, 0, 0), Is.EqualTo("#FF0000"));
            Assert.That(ThemeColorMath.RgbToHex(0, 128, 255), Is.EqualTo("#0080FF"));
        }

        [Test]
        public void TryParseHex_AcceptsCommonFormats()
        {
            Assert.That(ThemeColorMath.TryParseHex("#FF0000", out byte r1, out byte g1, out byte b1), Is.True);
            Assert.That((r1, g1, b1), Is.EqualTo((255, 0, 0)));

            Assert.That(ThemeColorMath.TryParseHex("FF0000", out byte r2, out byte g2, out byte b2), Is.True);
            Assert.That((r2, g2, b2), Is.EqualTo((255, 0, 0)));

            Assert.That(ThemeColorMath.TryParseHex("#F00", out byte r3, out byte g3, out byte b3), Is.True);
            Assert.That((r3, g3, b3), Is.EqualTo((255, 0, 0)));

            // #AARRGGBB — alpha is ignored, RGB is parsed.
            Assert.That(ThemeColorMath.TryParseHex("#80FF0000", out byte r4, out byte g4, out byte b4), Is.True);
            Assert.That((r4, g4, b4), Is.EqualTo((255, 0, 0)));
        }

        [Test]
        public void TryParseHex_Invalid_ReturnsFalse()
        {
            Assert.That(ThemeColorMath.TryParseHex("#GGGGGG", out _, out _, out _), Is.False);
            Assert.That(ThemeColorMath.TryParseHex("", out _, out _, out _), Is.False);
            Assert.That(ThemeColorMath.TryParseHex(null, out _, out _, out _), Is.False);
            Assert.That(ThemeColorMath.TryParseHex("#12345", out _, out _, out _), Is.False);
        }

        [Test]
        public void HueToSeedHex_UsesDefaultSaturationAndValue()
        {
            Assert.That(ThemeColorMath.HueToSeedHex(0), Is.EqualTo("#FF8080"));
            Assert.That(ThemeColorMath.HueToSeedHex(120), Is.EqualTo("#80FF80"));
            Assert.That(ThemeColorMath.HueToSeedHex(240), Is.EqualTo("#8080FF"));
        }
    }
}
