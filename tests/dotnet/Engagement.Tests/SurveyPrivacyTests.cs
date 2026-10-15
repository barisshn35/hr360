using EngagementService.Controllers;
using EngagementService.Infrastructure;
using Xunit;

namespace Engagement.Tests;

/// <summary>G18: yerel Türkçe duygu analizi, en küçük grup gizlemesi; G15: imha tarihi.</summary>
public class SurveyPrivacyTests
{
    [Theory]
    [InlineData("Ekip arkadaşlarım çok iyi, yöneticim destekliyor", TurkishSentiment.Label.Positive)]
    [InlineData("Burada çalışmaktan memnunum, harika bir ortam", TurkishSentiment.Label.Positive)]
    [InlineData("İş yükü çok yoğun ve stresli", TurkishSentiment.Label.Negative)]
    [InlineData("Maaşlar düşük, terfi süreci belirsiz", TurkishSentiment.Label.Negative)]
    [InlineData("Toplantı odaları üçüncü katta", TurkishSentiment.Label.Neutral)]
    [InlineData("", TurkishSentiment.Label.Neutral)]
    public void Duygu_etiketi(string text, TurkishSentiment.Label expected) =>
        Assert.Equal(expected, TurkishSentiment.Analyze(text).Label);

    [Theory]
    // "değil" sonraki sözcükte olumluyu çevirir
    [InlineData("Ortam iyi değil", TurkishSentiment.Label.Negative)]
    [InlineData("Yönetim adil değildi", TurkishSentiment.Label.Negative)]
    // fiil olumsuzluğu -me/-ma
    [InlineData("Yeni sistemi sevmiyorum", TurkishSentiment.Label.Negative)]
    [InlineData("Kararı hiç beğenmedim", TurkishSentiment.Label.Negative)]
    [InlineData("Artık zorlanmıyorum", TurkishSentiment.Label.Positive)]
    // mastar olumsuz sayılmaz
    [InlineData("Burada çalışmayı sevmek kolay", TurkishSentiment.Label.Positive)]
    // -sız/-siz eki
    [InlineData("Ekip desteksiz kaldı", TurkishSentiment.Label.Negative)]
    [InlineData("Herkes huzursuz", TurkishSentiment.Label.Negative)]
    [InlineData("Geçiş sorunsuz oldu", TurkishSentiment.Label.Positive)]
    // olumlu + "yok"
    [InlineData("Hiç destek yok", TurkishSentiment.Label.Negative)]
    // "kötü değil"
    [InlineData("Yemekler kötü değil", TurkishSentiment.Label.Positive)]
    // yanlış önek eşleşmesi olmamalı
    [InlineData("Eğitim seviyesi ve zorunlu kurslar", TurkishSentiment.Label.Neutral)]
    public void Olumsuzluk_ve_ekler(string text, TurkishSentiment.Label expected) =>
        Assert.Equal(expected, TurkishSentiment.Analyze(text).Label);

    [Fact]
    public void Buyuk_harf_ve_noktalama_turkce()
    {
        Assert.Equal(TurkishSentiment.Label.Positive, TurkishSentiment.Analyze("İYİ! MÜKEMMEL.").Label);
        Assert.Equal(new[] { "ışık", "iyi" }, TurkishSentiment.Tokenize("IŞIK, İyi"));
    }

    [Fact]
    public void Ozet_sayimlar_ve_anahtar_sozcukler_en_az_iki_yanitta()
    {
        var texts = new[]
        {
            "Esnek çalışma saatleri harika",
            "Esnek çalışma güzel ama maaş düşük",
            "Maaş düşük, iş yükü yoğun",
            "Kantin menüsü",
            "Yöneticim destekliyor, esnek çalışma çok iyi",
        };
        var s = TurkishSentiment.Summarize(texts);
        Assert.Equal(5, s.Positive + s.Negative + s.Neutral);
        Assert.Equal(1, s.Neutral);
        Assert.True(s.Negative >= 1);
        Assert.Contains(s.TopKeywords, k => k.Word == "esnek" && k.Count == 3);
        Assert.Contains(s.TopKeywords, k => k.Word == "maaş" && k.Count == 2);
        // tek yanıtta geçen sözcük listelenmez
        Assert.DoesNotContain(s.TopKeywords, k => k.Word == "kantin");
        // durak sözcükler listelenmez
        Assert.DoesNotContain(s.TopKeywords, k => k.Word == "ama");
    }

    [Fact]
    public void Kucuk_gruplar_gizlenir_ve_ikincil_gizleme()
    {
        // 5'ten küçük tek hücre (3) toplamdan çıkarılarak bulunmasın diye en küçük görünür hücre (6) de gizlenir.
        var hidden = SurveysController.HiddenGroups(new Dictionary<string, int> { ["A"] = 10, ["B"] = 6, ["C"] = 3 });
        Assert.Equal(new HashSet<string> { "B", "C" }, hidden);
        // Gizlenenlerin toplamı zaten >= 5 ise ikincil gizleme gerekmez.
        hidden = SurveysController.HiddenGroups(new Dictionary<string, int> { ["A"] = 10, ["B"] = 3, ["C"] = 2 });
        Assert.Equal(new HashSet<string> { "B", "C" }, hidden);
        // Hepsi büyükse hiçbiri gizlenmez.
        Assert.Empty(SurveysController.HiddenGroups(new Dictionary<string, int> { ["A"] = 5, ["B"] = 7 }));
        Assert.Equal(5, SurveysController.AnonymityThreshold);
    }

    [Fact]
    public void Imha_tarihi_saklama_suresine_gore() =>
        Assert.Equal(new DateOnly(2036, 10, 31), OffboardingController.PlannedAnonymization(new DateOnly(2026, 10, 31), 120));
}
