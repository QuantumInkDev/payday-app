using QRCoder;

namespace PayDay.Sync.Crypto;

/// <summary>
/// The pairing QR code as a PNG (<c>sync-protocol.md</c> 9.1 step 3), drawn with QRCoder (MIT) without
/// System.Drawing. The text is uppercase hex and colons, so the encoder uses QR alphanumeric mode:
/// 106 characters fit version 5 at error correction level M (37 × 37 modules), which phones and webcams
/// read easily from a screen.
/// </summary>
public static class PairingQrImage
{
    /// <summary>The QR code of <paramref name="qrText"/> as PNG bytes, black on white with the standard quiet zone.</summary>
    public static byte[] Png(string qrText, int pixelsPerModule = 8)
    {
        ArgumentException.ThrowIfNullOrEmpty(qrText);
        if (pixelsPerModule is < 1 or > 40) throw new ArgumentOutOfRangeException(nameof(pixelsPerModule));
        using var data = Encode(qrText);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }

    /// <summary>The QR symbol (exposed for tests: its version shows which encoding mode was used).</summary>
    internal static QRCodeData Encode(string qrText)
    {
        using var generator = new QRCodeGenerator();
        return generator.CreateQrCode(qrText, QRCodeGenerator.ECCLevel.M);
    }
}
