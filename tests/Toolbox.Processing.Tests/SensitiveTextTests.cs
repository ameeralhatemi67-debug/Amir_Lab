using Toolbox.Processing.Ocr;

namespace Toolbox.Processing.Tests;

/// <summary>Finding personal information in recognised text (the blur tool's "Find personal info"), and where its box goes.</summary>
public sealed class SensitiveTextTests
{
    /// <summary>A line of words 10 px per character with 6 px spaces, 20 px tall at y 100.</summary>
    private static OcrLine Line(string text, bool rtl = false)
    {
        var words = new List<OcrWord>();
        double x = 10;
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            words.Add(new OcrWord(w, x, 100, w.Length * 10, 20));
            x += w.Length * 10 + 6;
        }
        return new OcrLine(text, words, rtl ? "ar" : "en") { RightToLeft = rtl };
    }

    private static IReadOnlyList<SensitiveHit> Find(string text) => SensitiveText.Find(Line(text));

    [Theory]
    [InlineData("Write to amir.alhatemi@gmail.com today", "amir.alhatemi@gmail.com")]
    [InlineData("Email: first.last+news@company.co.uk", "first.last+news@company.co.uk")]
    [InlineData("contact amir @ example.org", "amir @ example.org")] // OCR put spaces round @
    public void Emails(string line, string expected)
    {
        var hit = Assert.Single(Find(line), h => h.Kind == SensitiveKind.Email);
        Assert.Equal(expected, hit.Text);
    }

    [Theory]
    [InlineData("Call +964 770 123 4567 now")]
    [InlineData("Mobile 0770 123 4567")]
    [InlineData("Office (555) 123-4567")]
    [InlineData("WhatsApp 00964 7701234567")]
    [InlineData("الهاتف ٠٧٧٠١٢٣٤٥٦٧")]
    public void Phones(string line) => Assert.Single(Find(line), h => h.Kind == SensitiveKind.Phone);

    [Theory]
    [InlineData("Due 2026-11-13 at noon")]
    [InlineData("Date 13/11/2026")]
    [InlineData("Order 12345678")]
    [InlineData("Total 1.250.000 IQD")]
    [InlineData("Version 10 build 22631")]
    public void Not_phones(string line) => Assert.DoesNotContain(Find(line), h => h.Kind == SensitiveKind.Phone);

    [Fact]
    public void Cards_are_luhn_checked()
    {
        Assert.Single(Find("Card 4242 4242 4242 4242 exp 12/28"), h => h.Kind == SensitiveKind.Card);
        Assert.Single(Find("visa 4111-1111-1111-1111"), h => h.Kind == SensitiveKind.Card);
        Assert.DoesNotContain(Find("Ref 1234 5678 9012 3456"), h => h.Kind == SensitiveKind.Card);
    }

    [Fact]
    public void Ibans_are_checksum_checked()
    {
        Assert.Single(Find("IBAN GB82 WEST 1234 5698 7654 32"), h => h.Kind == SensitiveKind.Iban);
        Assert.DoesNotContain(Find("IBAN GB00 WEST 1234 5698 7654 32"), h => h.Kind == SensitiveKind.Iban);
    }

    [Fact]
    public void Secrets_and_addresses()
    {
        Assert.Single(Find("key sk-ant-api03-AbCdEf123456GhIjKl7890"), h => h.Kind == SensitiveKind.Secret);
        Assert.Single(Find("token ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4"), h => h.Kind == SensitiveKind.Secret);
        var password = Assert.Single(Find("password: hunter2!"), h => h.Kind == SensitiveKind.Secret);
        Assert.Equal("hunter2!", password.Text);
        Assert.Single(Find("كلمة المرور: Qw8!xz"), h => h.Kind == SensitiveKind.Secret);
        Assert.Single(Find("server 192.168.1.20 is up"), h => h.Kind == SensitiveKind.IpAddress);
        Assert.Single(Find("id Xq7Lm2Pz9Rt4Kw8Nv3Bc6Hd1Fs5"), h => h.Kind == SensitiveKind.Secret);
        Assert.Empty(Find("C:/Users/You/Documents/Reports/Annual_report_final"));
        Assert.Empty(Find("Nothing private in this ordinary sentence of text."));
    }

    [Fact]
    public void One_thing_is_found_once()
    {
        // A card number is not also a phone number; an e-mail is one hit.
        var hits = Find("4242 4242 4242 4242 and amir@x.io");
        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Kind == SensitiveKind.Card);
        Assert.Contains(hits, h => h.Kind == SensitiveKind.Email);
    }

    [Fact]
    public void The_box_covers_the_words_trimmed_to_the_characters()
    {
        // "Email:amir@x.io" is one word; only "amir@x.io" (9 of its 15 characters) is boxed.
        var hit = Assert.Single(SensitiveText.Find(Line("Email:amir@x.io")));
        double wordX = 10, charW = 10;
        double pad = Math.Max(2, 20 * 0.18);
        Assert.Equal(wordX + 6 * charW - pad, hit.X, 3);
        Assert.Equal(9 * charW + 2 * pad, hit.Width, 3);
        Assert.Equal(100 - pad, hit.Y, 3);
        Assert.Equal(20 + 2 * pad, hit.Height, 3);
        // A phone number over four words: one box over all four.
        var phone = Assert.Single(Find("+964 770 123 4567"));
        Assert.Equal(10 - pad, phone.X, 3);
        Assert.Equal((4 + 3 + 3 + 4) * 10 + 3 * 6 + 2 * pad, phone.Width, 3);
    }

    [Fact]
    public void Masked_text_hides_most_of_it()
    {
        Assert.Equal("am•••@gmail.com", new SensitiveHit(SensitiveKind.Email, "amir@gmail.com", 0, 0, 1, 1).Masked);
        Assert.Equal("•••• 4242", new SensitiveHit(SensitiveKind.Card, "4242 4242 4242 4242", 0, 0, 1, 1).Masked);
        Assert.Equal("sk-•••", new SensitiveHit(SensitiveKind.Secret, "sk-ant-abcdefabcdef", 0, 0, 1, 1).Masked);
    }
}
