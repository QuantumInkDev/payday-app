using System;
using System.Linq;
using PayDay.Sync.Crypto;

namespace PayDay.Tests.Sync;

/// <summary>The pairing QR image (<c>sync-protocol.md</c> 9.1 step 3). The code is random each run.</summary>
public sealed class PairingQrImageTests
{
    [Fact]
    public void ThePairingText_IsEncodedInAlphanumericMode_AsAVersion5Symbol()
    {
        var text = PairingQr.Format(PairingQr.NewCode());
        Assert.Equal(106, text.Length);

        using var data = PairingQrImage.Encode(text);

        // 106 characters need version 6 in byte mode but fit version 5 (37 x 37) in alphanumeric mode.
        Assert.Equal(5, data.Version);
    }

    [Fact]
    public void ThePng_HasTheQuietZone_AndScalesWithThePixelSize()
    {
        var text = PairingQr.Format(PairingQr.NewCode());

        var small = PairingQrImage.Png(text, 4);
        var large = PairingQrImage.Png(text, 8);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, small.Take(8));
        // IHDR width: (37 modules + 2 x 4 quiet-zone modules) x pixels per module.
        Assert.Equal((37 + 8) * 4, ReadWidth(small));
        Assert.Equal((37 + 8) * 8, ReadWidth(large));
        Assert.Throws<ArgumentOutOfRangeException>(() => PairingQrImage.Png(text, 0));
    }

    private static int ReadWidth(byte[] png) => (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
}
