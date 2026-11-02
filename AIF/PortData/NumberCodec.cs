using System;
using System.Globalization;
using System.Text;

namespace Mpai.Aif.PortData;

// number - a number, as the AIF's type system carries it (a Basic type: the Prediction Horizon of CUI-CPP in months, a probability).
//
// NOT AN MPAI DATA TYPE, and so no schema to check it against. Inside and on the wire it is the same JSON literal.
public sealed class NumberCodec : IPortDataCodec
{
    public string DataType => "number";

    public byte[] ToWire(string internalJson) => Encoding.UTF8.GetBytes(Parse(internalJson).ToString("R", CultureInfo.InvariantCulture));

    public string ToInternal(byte[] wire) => Parse(Encoding.UTF8.GetString(wire)).ToString("R", CultureInfo.InvariantCulture);

    private static double Parse(string text) =>
        double.TryParse(text.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : throw new FormatException($"'{text}' is not a number.");
}
