using System.Collections.Generic;
using NUnit.Framework;
using VibeCoder.Core.Services;

namespace VibeCoder.Tests
{
    [TestFixture]
    public class QuestionOptionsCodecTests
    {
        [Test]
        public void RoundTrip_PreservesOptions()
        {
            var options = new List<string> { "Parser.cs", "Lexer.cs", "Both" };

            var encoded = QuestionOptionsCodec.Encode(options);
            var decoded = QuestionOptionsCodec.Decode(encoded);

            Assert.That(decoded, Is.EqualTo(options));
        }

        [Test]
        public void Encode_NullOrEmpty_ReturnsEmptyString()
        {
            Assert.That(QuestionOptionsCodec.Encode(null), Is.EqualTo(""));
            Assert.That(QuestionOptionsCodec.Encode(new List<string>()), Is.EqualTo(""));
        }

        [Test]
        public void Decode_NullEmptyOrInvalid_ReturnsEmptyList()
        {
            Assert.That(QuestionOptionsCodec.Decode(null), Is.Empty);
            Assert.That(QuestionOptionsCodec.Decode(""), Is.Empty);
            Assert.That(QuestionOptionsCodec.Decode("   "), Is.Empty);
            Assert.That(QuestionOptionsCodec.Decode("not-json"), Is.Empty);
        }

        [Test]
        public void RoundTrip_PreservesSpecialCharacters()
        {
            var options = new List<string>
            {
                "Add a \"null\" guard",
                "Fix: comma, quotes \" and \\ slash",
                "Перевести на русский",
                "Line1\nLine2\tTabbed"
            };

            var decoded = QuestionOptionsCodec.Decode(QuestionOptionsCodec.Encode(options));

            Assert.That(decoded, Is.EqualTo(options));
        }
    }
}
