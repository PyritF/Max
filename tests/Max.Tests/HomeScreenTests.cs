using Max.Ui;

namespace Max.Tests;

public class HomeScreenTests
{
    [Theory]
    [InlineData(5, 0, "Guten Morgen, alex.")]
    [InlineData(11, 59, "Guten Morgen, alex.")]
    [InlineData(12, 0, "Guten Tag, alex.")]
    [InlineData(17, 59, "Guten Tag, alex.")]
    [InlineData(18, 0, "Guten Abend, alex.")]
    [InlineData(22, 59, "Guten Abend, alex.")]
    [InlineData(23, 0, "Noch wach, alex?")]
    [InlineData(0, 0, "Noch wach, alex?")]
    [InlineData(4, 59, "Noch wach, alex?")]
    public void GetGreeting_passt_zur_Tageszeit(int stunde, int minute, string erwartet)
    {
        var zeit = new DateTime(2026, 1, 1, stunde, minute, 0);

        Assert.Equal(erwartet, HomeScreen.GetGreeting(zeit, "alex"));
    }
}
