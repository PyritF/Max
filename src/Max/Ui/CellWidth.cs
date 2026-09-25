using System.Globalization;
using System.Text;

namespace Max.Ui;

/// <summary>
/// Wie viele Terminal-Spalten ein Zeichen belegt: 2 für Emojis und ostasiatische Schriftzeichen,
/// sonst 1. Genau genug für den Zeilenumbruch – vollständige Unicode-Tabellen wären Overkill.
/// </summary>
internal static class CellWidth
{
    /// <summary>Breite eines Graphems (z. B. "a", "ü", "👍", "👍🏽", "❤️").</summary>
    public static int Of(string grapheme)
    {
        if (grapheme.Length == 0)
            return 0;
        foreach (var rune in grapheme.EnumerateRunes())
        {
            if (rune.Value == 0xFE0F || IsWide(rune.Value))
                return 2; // Emoji-Darstellung erzwungen oder breites Zeichen
        }
        return 1;
    }

    /// <summary>Breite eines ganzen Texts.</summary>
    public static int OfText(string text)
    {
        var width = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
            width += Of(elements.GetTextElement());
        return width;
    }

    private static bool IsWide(int c) =>
        c is >= 0x1100 and <= 0x115F          // Hangul Jamo
          or >= 0x2E80 and <= 0x303E           // CJK-Radikale, Satzzeichen
          or >= 0x3041 and <= 0x33FF           // Kana, CJK-Kompatibilität
          or >= 0x3400 and <= 0x4DBF           // CJK Erweiterung A
          or >= 0x4E00 and <= 0x9FFF           // CJK
          or >= 0xA000 and <= 0xA4CF           // Yi
          or >= 0xAC00 and <= 0xD7A3           // Hangul
          or >= 0xF900 and <= 0xFAFF           // CJK-Kompatibilität
          or >= 0xFE30 and <= 0xFE4F
          or >= 0xFF00 and <= 0xFF60           // Vollbreite Formen
          or >= 0xFFE0 and <= 0xFFE6
          or >= 0x1F300 and <= 0x1F64F         // Symbole, Emoticons
          or >= 0x1F680 and <= 0x1F6FF         // Verkehr, Karten
          or >= 0x1F900 and <= 0x1F9FF         // weitere Emojis
          or >= 0x1FA70 and <= 0x1FAFF
          or >= 0x20000 and <= 0x3FFFD;        // CJK Erweiterungen B+
}
