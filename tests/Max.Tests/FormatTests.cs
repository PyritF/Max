using Max.Ui;

namespace Max.Tests;

public class FormatTests
{
    [Fact]
    public void Gigabytes_mit_einer_Nachkommastelle_und_Komma()
    {
        Assert.Equal("3,4", Format.Gigabytes(3_400_000_000));
        Assert.Equal("0,0", Format.Gigabytes(0));
    }

    [Fact]
    public void Memory_rundet_auf_ganze_GB()
    {
        Assert.Equal("16 GB", Format.Memory(17_070_000_000));
    }

    [Fact]
    public void Speed_in_MB_pro_Sekunde()
    {
        Assert.Equal("38,2 MB/s", Format.Speed(38_200_000));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59.2, "1:00")]
    [InlineData(112, "1:52")]
    [InlineData(3725, "1:02:05")]
    public void Duration_als_Minuten_und_Sekunden(double sekunden, string erwartet)
    {
        Assert.Equal(erwartet, Format.Duration(TimeSpan.FromSeconds(sekunden)));
    }

    [Fact]
    public void ShortenPath_laesst_kurze_Pfade_unveraendert()
    {
        Assert.Equal(@"C:\Max", Format.ShortenPath(@"C:\Max", 40));
    }

    [Fact]
    public void ShortenPath_kuerzt_lange_Pfade_in_der_Mitte()
    {
        var pfad = @"C:\Users\alex\source\repos\PyritF\Max\src\Max\bin\Debug";

        var kurz = Format.ShortenPath(pfad, 30);

        Assert.Equal(30, kurz.Length);
        Assert.Contains('…', kurz);
        Assert.StartsWith(@"C:\", kurz);
        Assert.EndsWith(@"bin\Debug", kurz);
    }
}
