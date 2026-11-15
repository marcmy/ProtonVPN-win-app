using System.Text;
using ProtonVPN.OperatingSystems.WebAuthn.Serialization;

namespace ProtonVPN.OperatingSystems.WebAuthn.Tests.Serialization;

[TestClass]
public class Base64UrlConverterTest
{
    [TestMethod]
    public void EmptySpan_DecodesLikeEmptyString()
    {
        CollectionAssert.AreEqual(
            Base64UrlConverter.FromBase64UrlString(string.Empty),
            Base64UrlConverter.FromBase64UrlString(ReadOnlySpan<byte>.Empty));
    }

    [TestMethod]
    [DataRow("Zg")]
    [DataRow("Zm8")]
    [DataRow("Zm9v")]
    [DataRow("-_8")]
    public void SpanAndStringDecoders_Agree(string input)
    {
        CollectionAssert.AreEqual(
            Base64UrlConverter.FromBase64UrlString(input),
            Base64UrlConverter.FromBase64UrlString(Encoding.ASCII.GetBytes(input).AsSpan()));
    }
}
