using System.IO.Compression;
using System.Text;
using LearningService.Services;
using Xunit;

namespace Learning.Tests;

/// <summary>Yüklenen dosyalarda gerçek tür denetimi (magic bytes). Aynı sınıfın kopyaları: tenant, expense, governance.</summary>
public class FileSnifferTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46];
    private static readonly byte[] WebpBytes = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ");
    private static readonly byte[] GifBytes = Encoding.ASCII.GetBytes("GIF89a\x01\0\x01\0");
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n%âãÏÓ\n");
    private static readonly string[] Images = [FileSniffer.Png, FileSniffer.Jpeg, FileSniffer.Webp];

    private static byte[] RealZip()
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var w = new StreamWriter(z.CreateEntry("imsmanifest.xml").Open())) w.Write("<manifest/>");
        return ms.ToArray();
    }

    [Theory]
    [InlineData("png", FileSniffer.Png)]
    [InlineData("jpeg", FileSniffer.Jpeg)]
    [InlineData("webp", FileSniffer.Webp)]
    [InlineData("gif", FileSniffer.Gif)]
    [InlineData("pdf", FileSniffer.Pdf)]
    public void Imzadan_tur_tanınır(string kind, string expected)
    {
        var data = kind switch { "png" => PngBytes, "jpeg" => JpegBytes, "webp" => WebpBytes, "gif" => GifBytes, _ => PdfBytes };
        Assert.Equal(expected, FileSniffer.Detect(data));
    }

    [Fact]
    public void Gercek_zip_ve_bos_zip_tanınır_metin_ve_svg_tanınmaz()
    {
        Assert.Equal(FileSniffer.Zip, FileSniffer.Detect(RealZip()));
        Assert.Equal(FileSniffer.Zip, FileSniffer.Detect([0x50, 0x4B, 0x05, 0x06, 0, 0]));
        Assert.Null(FileSniffer.Detect("<svg onload=alert(1)>"u8));
        Assert.Null(FileSniffer.Detect("MZ\x90\0"u8));
        Assert.Null(FileSniffer.Detect([]));
    }

    [Fact]
    public void Gecerli_goruntu_bildirilen_tur_ve_uzantiyla_kabul_edilir()
    {
        Assert.Equal(FileSniffer.Verdict.Ok, FileSniffer.Check(PngBytes, "image/png", "fis.png", Images, out var d));
        Assert.Equal(FileSniffer.Png, d);
        // image/jpg ve genel türler (octet-stream, boş) kabul; karar içerikten
        Assert.Equal(FileSniffer.Verdict.Ok, FileSniffer.Check(JpegBytes, "image/jpg", "a.JPG", Images, out _));
        Assert.Equal(FileSniffer.Verdict.Ok, FileSniffer.Check(WebpBytes, "application/octet-stream", null, Images, out _));
        Assert.Equal(FileSniffer.Verdict.Ok, FileSniffer.Check(JpegBytes, null, "receipt", Images, out _));
    }

    [Fact]
    public void Bildirilen_tur_ya_da_uzanti_icerikle_uyusmazsa_reddedilir()
    {
        Assert.Equal(FileSniffer.Verdict.Mismatch, FileSniffer.Check(JpegBytes, "image/png", "a.png", Images, out _));
        Assert.Equal(FileSniffer.Verdict.Mismatch, FileSniffer.Check(PngBytes, "image/png", "a.jpg", Images, out _));
        Assert.Equal(FileSniffer.Verdict.Mismatch, FileSniffer.Check(PngBytes, "text/html", "a.png", Images, out _));
    }

    [Fact]
    public void Izinli_olmayan_ya_da_taninmayan_icerik_reddedilir()
    {
        // .png adı ve image/png türüyle gönderilen SVG/HTML
        Assert.Equal(FileSniffer.Verdict.NotAllowed, FileSniffer.Check("<svg xmlns='http://www.w3.org/2000/svg'><script>x</script></svg>"u8, "image/png", "logo.png", Images, out _));
        Assert.Equal(FileSniffer.Verdict.NotAllowed, FileSniffer.Check(PdfBytes, "image/jpeg", "a.jpg", Images, out _));
        Assert.Equal(FileSniffer.Verdict.NotAllowed, FileSniffer.Check(GifBytes, "image/gif", "a.gif", Images, out _));
    }

    [Fact]
    public void Zip_windows_turu_ile_kabul_ama_zip_adli_baska_icerik_reddedilir()
    {
        string[] zip = [FileSniffer.Zip];
        Assert.Equal(FileSniffer.Verdict.Ok, FileSniffer.Check(RealZip(), "application/x-zip-compressed", "paket.zip", zip, out _));
        Assert.Equal(FileSniffer.Verdict.Ok, FileSniffer.Check(RealZip(), "application/zip", "p.zip", zip, out _));
        Assert.Equal(FileSniffer.Verdict.NotAllowed, FileSniffer.Check("MZ\x90\0\x03"u8, "application/zip", "p.zip", zip, out _));
        Assert.Equal(FileSniffer.Verdict.Mismatch, FileSniffer.Check(RealZip(), "application/zip", "p.pdf", zip, out _));
    }

    [Fact]
    public void Metin_yalnizca_NUL_icermeyen_gecerli_UTF8_ise_kabul()
    {
        string[] text = [FileSniffer.Text];
        Assert.Equal(FileSniffer.Verdict.Ok, FileSniffer.Check(Encoding.UTF8.GetBytes("ad;soyad\nAyşe;Yılmaz\n"), "text/csv", "liste.csv", text, out var d));
        Assert.Equal(FileSniffer.Text, d);
        Assert.Equal(FileSniffer.Verdict.NotAllowed, FileSniffer.Check("a\0b"u8, "text/plain", "a.txt", text, out _));
        Assert.Equal(FileSniffer.Verdict.NotAllowed, FileSniffer.Check([0x41, 0xFD, 0x42], "text/plain", "a.txt", text, out _)); // cp1254 'ı'
        Assert.Equal(FileSniffer.Verdict.Mismatch, FileSniffer.Check("<b>x</b>"u8, "text/html", "a.txt", text, out _));
    }
}
