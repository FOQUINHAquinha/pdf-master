using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFMaster.Conversion;
using PDFMaster.Core.Files;
using PDFMaster.Domain;
using PDFMaster.Editor;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;
using PDFMaster.Security;
using Xunit;
using Xunit.Abstractions;
using PdfMetadata = PDFMaster.Security.PdfMetadata;

namespace PDFMaster.Tests;

/// <summary>Amostras da Fase 6: PDFs assinados (escritos byte a byte), danificados e com recursos proibidos no PDF/A.</summary>
public static class SecuritySamples
{
    /// <summary>PDF com uma página de texto em Helvetica por item de <paramref name="pages"/>.</summary>
    public static string Pages(string path, params string[][] pages)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var document = new PdfDocument();
        var font = new PdfDictionary(document);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfName("/Helvetica");
        font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        document.Internals.AddObject(font);
        foreach (var lines in pages)
        {
            var page = document.AddPage();
            page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(595, 842));
            var resources = new PdfDictionary(document);
            var fonts = new PdfDictionary(document);
            fonts.Elements["/F1"] = font.Reference!;
            resources.Elements["/Font"] = fonts;
            page.Elements["/Resources"] = resources;
            var content = new StringBuilder();
            var y = 760;
            foreach (var line in lines)
            {
                content.Append(CultureInfo.InvariantCulture, $"BT /F1 16 Tf 72 {y} Td {Pdf.Literal(Pdf.WinAnsi(line))} Tj ET\n");
                y -= 30;
            }

            page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(content.ToString()));
        }

        document.Save(path);
        return path;
    }

    public static X509Certificate2 SelfSigned(string name, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}, O=PDF Master Testes, C=BR", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    /// <summary>Raiz de teste e certificado do signatário emitido por ela.</summary>
    public static (X509Certificate2 Root, X509Certificate2 Leaf) Chain(string name)
    {
        var now = DateTimeOffset.UtcNow;
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Raiz de Teste PDF Master, O=PDF Master Testes, C=BR", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        var root = rootRequest.CreateSelfSigned(now.AddDays(-2), now.AddYears(5));

        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest($"CN={name}, O=PDF Master Testes, C=BR", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var issued = leafRequest.Create(root, now.AddDays(-1), now.AddYears(1), RandomNumberGenerator.GetBytes(8));
        return (root, issued.CopyWithPrivateKey(leafKey));
    }

    /// <summary>
    /// PDF de uma página assinado digitalmente (PKCS#7 destacado, SHA-256), escrito byte a byte para
    /// controlar exatamente a faixa assinada.
    /// </summary>
    public static byte[] Signed(X509Certificate2 certificate, DateTimeOffset signingTime, string text = "Documento assinado")
    {
        const int hexLength = 16384;
        const string placeholder = "[0 0000000000 0000000000 0000000000]";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /SigFlags 3 >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 6 0 R >> >> /Annots [5 0 R] >>",
            null,
            "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Assinatura1) /Rect [0 0 0 0] /F 132 /P 3 0 R /V 7 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /Name (Signatario de Teste) /Reason (Teste automatizado) " +
            $"/Location (Brasil) /M (D:{signingTime.UtcDateTime:yyyyMMddHHmmss}Z) /ByteRange {placeholder} /Contents <{new string('0', hexLength)}> >>",
        };
        var content = $"BT /F1 24 Tf 72 760 Td ({text}) Tj ET";

        var file = new MemoryStream();
        void Write(string s) => file.Write(Encoding.Latin1.GetBytes(s));
        Write("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new long[objects.Length];
        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = file.Position;
            Write(i == 3
                ? $"{i + 1} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"
                : $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = file.Position;
        Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f\r\n");
        foreach (var offset in offsets) Write($"{offset:D10} 00000 n\r\n");
        Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /ID [<00112233445566778899AABBCCDDEEFF><00112233445566778899AABBCCDDEEFF>] >>\nstartxref\n{xref}\n%%EOF\n");
        var bytes = file.ToArray();

        var latin = Encoding.Latin1.GetString(bytes);
        var contentsStart = latin.IndexOf("/Contents <", StringComparison.Ordinal) + "/Contents ".Length;
        var contentsEnd = contentsStart + hexLength + 2;
        var range = $"[0 {contentsStart} {contentsEnd} {bytes.Length - contentsEnd}]".PadRight(placeholder.Length);
        var rangeAt = latin.IndexOf(placeholder, StringComparison.Ordinal);
        Encoding.ASCII.GetBytes(range).CopyTo(bytes, rangeAt);

        var signed = bytes[..contentsStart].Concat(bytes[contentsEnd..]).ToArray();
        var cms = new SignedCms(new ContentInfo(signed), detached: true);
        var signer = new CmsSigner(certificate) { IncludeOption = X509IncludeOption.EndCertOnly, DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") };
        signer.SignedAttributes.Add(new Pkcs9SigningTime(signingTime.UtcDateTime));
        cms.ComputeSignature(signer, silent: true);
        var hex = Convert.ToHexString(cms.Encode());
        Encoding.ASCII.GetBytes(hex).CopyTo(bytes, contentsStart + 1);
        return bytes;
    }

    /// <summary>Atualização incremental (um objeto novo) acrescentada ao fim de um PDF de xref clássica.</summary>
    public static byte[] AppendUpdate(byte[] pdf)
    {
        var latin = Encoding.Latin1.GetString(pdf);
        var startxref = latin.LastIndexOf("startxref", StringComparison.Ordinal);
        var previous = latin[(startxref + 9)..].Trim().Split('\n')[0].Trim();
        var update = new StringBuilder("\n");
        var offset = pdf.Length + 1;
        update.Append("8 0 obj\n<< /Teste true >>\nendobj\n");
        var xref = pdf.Length + Encoding.Latin1.GetByteCount(update.ToString());
        update.Append(CultureInfo.InvariantCulture, $"xref\n0 1\n0000000000 65535 f\r\n8 1\n{offset:D10} 00000 n\r\n");
        update.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size 9 /Root 1 0 R /Prev {previous} >>\nstartxref\n{xref}\n%%EOF\n");
        return [.. pdf, .. Encoding.Latin1.GetBytes(update.ToString())];
    }
}

public class ProtectionTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PdfProtection _protection = new(new OutputPlanner(new OutputPathResolver()));

    public void Dispose() => _temp.Dispose();

    private OutputTarget Target => new(Path.Combine(_temp.Path, "saida"), OverwritePolicy.AutoRename, SubfolderMode.None);

    private string Sample() => EditorSamples.TextPdf(Path.Combine(_temp.Path, $"doc-{Guid.NewGuid():N}.pdf"), pages: 2);

    [Fact]
    public async Task Protecting_with_passwords_encrypts_with_aes_and_applies_permissions()
    {
        var source = Sample();
        var before = File.ReadAllBytes(source);
        var allowed = PdfPermissions.All with { Copy = false, Modify = false, Print = true, HighQualityPrint = false };
        var result = await _protection.ProtectAsync(source, new ProtectOptions("abrir123", "dono4567", allowed), Target);

        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.EndsWith("_protegido.pdf", result.Path, StringComparison.Ordinal);
        Assert.Equal(2, result.PageCount);

        var locked = PdfProtection.Inspect(result.Path);
        Assert.False(locked.Opened);
        Assert.True(locked.PasswordRequired);
        Assert.False(PdfProtection.Inspect(result.Path, "errada").Opened);

        var user = PdfProtection.Inspect(result.Path, "abrir123");
        Assert.True(user.Opened);
        Assert.True(user.Encrypted);
        Assert.NotNull(user.Encryption);
        Assert.Contains("AES", user.Encryption!, StringComparison.Ordinal);
        Assert.False(user.Permissions!.Copy);
        Assert.False(user.Permissions.Modify);
        Assert.True(user.Permissions.Print);
        Assert.Contains("copiar texto e imagens", user.Permissions.Denied());
        Assert.Equal(2, user.PageCount);

        var owner = PdfProtection.Inspect(result.Path, "dono4567");
        Assert.True(owner.Opened);
        Assert.Equal(PdfPermissions.All, owner.Permissions);
    }

    [Fact]
    public async Task Owner_password_only_keeps_the_document_readable_but_restricted()
    {
        var source = Sample();
        var allowed = PdfPermissions.All with { Print = false, HighQualityPrint = false };
        var result = await _protection.ProtectAsync(source, new ProtectOptions(null, "segredo1", allowed), Target);

        var report = PdfProtection.Inspect(result.Path);
        Assert.True(report.Opened);
        Assert.True(report.Encrypted);
        Assert.False(report.Permissions!.Print);
        Assert.True(report.HasRestrictions);
        var page = await new PdfiumRenderer().RenderPageAsync(result.Path, 0, RenderSize.ByScale(0.5));
        Assert.True(page.Width > 0);
    }

    [Fact]
    public async Task Unlocking_requires_the_owner_password_and_produces_an_unprotected_copy()
    {
        var source = Sample();
        var protectedFile = (await _protection.ProtectAsync(source, new ProtectOptions("abrir123", "dono4567", PdfPermissions.All with { Copy = false }), Target)).Path;
        var before = File.ReadAllBytes(protectedFile);

        var wrong = await Assert.ThrowsAsync<PdfEngineException>(() => _protection.UnlockAsync(protectedFile, "errada", Target));
        Assert.Contains("incorreta", wrong.Message, StringComparison.Ordinal);
        var userOnly = await Assert.ThrowsAsync<PdfEngineException>(() => _protection.UnlockAsync(protectedFile, "abrir123", Target));
        Assert.Contains("proprietário", userOnly.Message, StringComparison.Ordinal);

        var unlocked = await _protection.UnlockAsync(protectedFile, "dono4567", Target);
        Assert.Equal(before, File.ReadAllBytes(protectedFile));
        Assert.EndsWith("_desprotegido.pdf", unlocked.Path, StringComparison.Ordinal);
        var report = PdfProtection.Inspect(unlocked.Path);
        Assert.True(report.Opened);
        Assert.False(report.Encrypted);
        Assert.Equal(PdfTextExporter.ReadPages(source), PdfTextExporter.ReadPages(unlocked.Path));
    }

    [Fact]
    public async Task Invalid_requests_are_refused_with_clear_messages()
    {
        var source = Sample();
        await Assert.ThrowsAsync<PdfEngineException>(() => _protection.ProtectAsync(source, new ProtectOptions("12", null, PdfPermissions.All), Target));
        await Assert.ThrowsAsync<PdfEngineException>(() => _protection.ProtectAsync(source, new ProtectOptions(null, null, PdfPermissions.All), Target));
        await Assert.ThrowsAsync<PdfEngineException>(() => _protection.ProtectAsync(source, new ProtectOptions(null, "segredo1", PdfPermissions.All), Target));
        var protectedFile = (await _protection.ProtectAsync(source, new ProtectOptions(null, "segredo1", PdfPermissions.All with { Modify = false }), Target)).Path;
        var again = await Assert.ThrowsAsync<PdfEngineException>(() =>
            _protection.ProtectAsync(protectedFile, new ProtectOptions("outra123", null, PdfPermissions.All), Target));
        Assert.Contains("já está protegido", again.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<PdfEngineException>(() => _protection.UnlockAsync(source, "qualquer", Target));
    }
}

public class MetadataTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PdfMetadata _metadata = new(new OutputPlanner(new OutputPathResolver()));

    public void Dispose() => _temp.Dispose();

    private OutputTarget Target => new(Path.Combine(_temp.Path, "saida"), OverwritePolicy.AutoRename, SubfolderMode.None);

    [Fact]
    public async Task Edited_properties_are_written_exactly_including_producer_and_dates()
    {
        var source = EditorSamples.TextPdf(Path.Combine(_temp.Path, "doc.pdf"));
        var before = File.ReadAllBytes(source);
        var created = new DateTime(2024, 3, 15, 10, 30, 0, DateTimeKind.Local);
        var modified = new DateTime(2026, 11, 1, 8, 5, 9, DateTimeKind.Local);
        var properties = new DocumentProperties("Escritura pública — São Paulo", "Gustavo Farias Medeiros", "Compra e venda",
            "imóvel; cartório", "Editor de teste", "Produtor de teste", created, modified);

        var result = await _metadata.WriteAsync(source, properties, Target);
        Assert.Equal(before, File.ReadAllBytes(source));
        var read = PdfMetadata.Read(result.Path);
        Assert.Equal(properties.Title, read.Title);
        Assert.Equal(properties.Author, read.Author);
        Assert.Equal(properties.Subject, read.Subject);
        Assert.Equal(properties.Keywords, read.Keywords);
        Assert.Equal(properties.Creator, read.Creator);
        Assert.Equal(properties.Producer, read.Producer);
        Assert.Equal(created, read.Created);
        Assert.Equal(modified, read.Modified);
        Assert.True(read.HasXmp);

        // O XMP acompanha as propriedades e o texto do documento não muda.
        using (var document = PdfReader.Open(result.Path, PdfDocumentOpenMode.Import))
        {
            var xmp = Encoding.UTF8.GetString(PdfMetadata.XmpBytes(document)!);
            Assert.Contains("Escritura pública — São Paulo", xmp, StringComparison.Ordinal);
            Assert.Contains("Produtor de teste", xmp, StringComparison.Ordinal);
        }

        Assert.Equal(PdfTextExporter.ReadPages(source), PdfTextExporter.ReadPages(result.Path));
        Assert.True(PdfAValidator.Validate(result.Path, PdfALevel.A2b).Checks.Single(c => c.Id == "metadados").Problems
            .All(p => !p.Contains("diferente", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Removing_metadata_leaves_no_properties_and_no_xmp()
    {
        var source = EditorSamples.TextPdf(Path.Combine(_temp.Path, "doc.pdf"));
        var titled = await _metadata.WriteAsync(source, new DocumentProperties("Título secreto", "Autor secreto", "Assunto", "chave", "Criador"), Target);
        var result = await _metadata.RemoveAllAsync(titled.Path, Target);

        var read = PdfMetadata.Read(result.Path);
        Assert.Equal(string.Empty, read.Title);
        Assert.Equal(string.Empty, read.Author);
        Assert.Equal(string.Empty, read.Subject);
        Assert.Equal(string.Empty, read.Keywords);
        Assert.Equal(string.Empty, read.Creator);
        Assert.Equal(string.Empty, read.Producer);
        Assert.Null(read.Created);
        Assert.False(read.HasXmp);
        var bytes = Encoding.Latin1.GetString(File.ReadAllBytes(result.Path));
        Assert.DoesNotContain("Autor secreto", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("Título", bytes, StringComparison.Ordinal);
        Assert.Equal(PdfTextExporter.ReadPages(source), PdfTextExporter.ReadPages(result.Path));
    }
}

public class SignatureTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void An_intact_signature_is_verified_cryptographically_but_a_self_signed_identity_is_not_trusted()
    {
        using var certificate = SecuritySamples.SelfSigned("Signatário de Teste", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var when = DateTimeOffset.UtcNow.AddMinutes(-5);
        var path = _temp.File("assinado.pdf", SecuritySamples.Signed(certificate, when));

        var report = Assert.Single(SignatureVerifier.Verify(path));
        Assert.Equal(SignatureIntegrity.Intact, report.Integrity);
        Assert.True(report.CoversWholeDocument);
        Assert.Equal(0, report.BytesAfterSignature);
        Assert.Equal("Signatário de Teste", report.SignerName);
        Assert.Contains("PDF Master Testes", report.Issuer, StringComparison.Ordinal);
        Assert.True(report.CertificateValidAtSigningTime);
        Assert.Equal(CertificateTrust.UntrustedRoot, report.Trust);
        Assert.Equal("Assinatura1", report.FieldName);
        Assert.Equal(0, report.PageIndex);
        Assert.Equal("Teste automatizado", report.Reason);
        Assert.Equal(when.ToUnixTimeSeconds(), report.SigningTime!.Value.ToUnixTimeSeconds());
        Assert.Equal("adbe.pkcs7.detached", report.SubFilter);
        Assert.False(report.HasTimestamp);
        Assert.Contains("não pôde ser confirmada", report.Verdict, StringComparison.Ordinal);
        Assert.Equal(1, SignatureVerifier.Count(path));
    }

    [Fact]
    public void Changing_a_single_signed_byte_breaks_the_signature()
    {
        using var certificate = SecuritySamples.SelfSigned("Signatário", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var bytes = SecuritySamples.Signed(certificate, DateTimeOffset.UtcNow);
        var at = Encoding.Latin1.GetString(bytes).IndexOf("Documento assinado", StringComparison.Ordinal);
        bytes[at] = (byte)'X';
        var path = _temp.File("adulterado.pdf", bytes);

        var report = Assert.Single(SignatureVerifier.Verify(path));
        Assert.Equal(SignatureIntegrity.Broken, report.Integrity);
        Assert.StartsWith("Assinatura INVÁLIDA", report.Verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_added_after_signing_is_reported_as_a_later_modification()
    {
        using var certificate = SecuritySamples.SelfSigned("Signatário", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var bytes = SecuritySamples.AppendUpdate(SecuritySamples.Signed(certificate, DateTimeOffset.UtcNow));
        var path = _temp.File("alterado.pdf", bytes);

        var report = Assert.Single(SignatureVerifier.Verify(path));
        Assert.Equal(SignatureIntegrity.IntactButModifiedLater, report.Integrity);
        Assert.False(report.CoversWholeDocument);
        Assert.True(report.BytesAfterSignature > 0);
        Assert.Contains(report.Notes, n => n.Contains("depois da assinatura", StringComparison.Ordinal));
    }

    [Fact]
    public void A_certificate_from_a_root_added_by_the_user_is_trusted_only_with_that_root()
    {
        var (root, leaf) = SecuritySamples.Chain("Pessoa Física de Teste");
        using (root)
        using (leaf)
        {
            var path = _temp.File("cadeia.pdf", SecuritySamples.Signed(leaf, DateTimeOffset.UtcNow));
            var withoutRoot = Assert.Single(SignatureVerifier.Verify(path));
            Assert.NotEqual(CertificateTrust.Trusted, withoutRoot.Trust);

            using var publicRoot = X509CertificateLoader.LoadCertificate(root.RawData);
            var withRoot = Assert.Single(SignatureVerifier.Verify(path, [publicRoot]));
            Assert.Equal(SignatureIntegrity.Intact, withRoot.Integrity);
            Assert.Equal(CertificateTrust.Trusted, withRoot.Trust);
            Assert.Equal(2, withRoot.Chain.Count);
            Assert.False(withRoot.IcpBrasil);
            Assert.Contains("cadeia confiável", withRoot.Verdict, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_expired_certificate_is_not_valid_at_the_signing_time()
    {
        using var certificate = SecuritySamples.SelfSigned("Vencido", DateTimeOffset.UtcNow.AddYears(-3), DateTimeOffset.UtcNow.AddYears(-2));
        var path = _temp.File("vencido.pdf", SecuritySamples.Signed(certificate, DateTimeOffset.UtcNow));
        var report = Assert.Single(SignatureVerifier.Verify(path));
        Assert.Equal(SignatureIntegrity.Intact, report.Integrity);
        Assert.False(report.CertificateValidAtSigningTime);
        Assert.Equal(CertificateTrust.Invalid, report.Trust);
    }

    [Fact]
    public void Unsigned_documents_have_no_signatures()
    {
        var path = EditorSamples.TextPdf(Path.Combine(_temp.Path, "simples.pdf"));
        Assert.Empty(SignatureVerifier.Verify(path));
        Assert.Equal(0, SignatureVerifier.Count(path));
    }
}

public class RepairTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PdfRepair _repair = new(new OutputPlanner(new OutputPathResolver()));

    public void Dispose() => _temp.Dispose();

    private OutputTarget Target => new(Path.Combine(_temp.Path, "saida"), OverwritePolicy.AutoRename, SubfolderMode.None);

    private string Sample(string name) =>
        SecuritySamples.Pages(Path.Combine(_temp.Path, name), ["Pagina um"], ["Pagina dois"], ["Pagina tres"], ["Pagina quatro"]);

    [Fact]
    public void A_healthy_file_has_no_problems()
    {
        var report = PdfRepair.Diagnose(Sample("sadio.pdf"));
        Assert.Equal(DamageLevel.None, report.Level);
        Assert.False(report.Damaged);
        Assert.True(report.XrefValid);
        Assert.True(report.StrictReaderOpens);
        Assert.Equal(4, report.PageCount);
        Assert.Equal(0, report.MissingObjects);
        Assert.Empty(report.Problems);
    }

    [Fact]
    public async Task Shifted_offsets_are_detected_and_rebuilt_without_losing_pages()
    {
        var source = Sample("deslocado.pdf");
        var expected = PdfTextExporter.ReadPages(source);
        var bytes = File.ReadAllBytes(source);
        var header = Array.IndexOf(bytes, (byte)'\n') + 1;
        var damaged = _temp.File("deslocado-danificado.pdf", [.. bytes[..header], .. Encoding.ASCII.GetBytes("% lixo inserido no meio do arquivo para deslocar tudo\n"), .. bytes[header..]]);

        var report = PdfRepair.Diagnose(damaged);
        Assert.True(report.Damaged);
        Assert.Equal(DamageLevel.Recoverable, report.Level);
        Assert.False(report.XrefValid);
        Assert.True(report.XrefWrongOffsets > 0);

        var result = await _repair.RepairAsync(damaged, null, Target);
        Assert.Equal(4, result.RecoveredPages);
        Assert.Empty(result.LostPages);
        var after = PdfRepair.Diagnose(result.File.Path);
        Assert.True(after.Problems.Count == 0, string.Join(" | ", after.Problems));
        Assert.Equal(DamageLevel.None, after.Level);
        Assert.Equal(expected, PdfTextExporter.ReadPages(result.File.Path));
    }

    [Fact]
    public async Task A_file_cut_at_the_end_is_recovered()
    {
        var source = Sample("cortado.pdf");
        var bytes = File.ReadAllBytes(source);
        var end = Encoding.Latin1.GetString(bytes).LastIndexOf("startxref", StringComparison.Ordinal);
        var damaged = _temp.File("cortado-danificado.pdf", bytes[..end]);

        var report = PdfRepair.Diagnose(damaged);
        Assert.False(report.HasEndMarker);
        Assert.True(report.Damaged);

        var result = await _repair.RepairAsync(damaged, null, Target);
        Assert.Equal(4, result.RecoveredPages);
        var problems = PdfRepair.Diagnose(result.File.Path).Problems;
        Assert.True(problems.Count == 0, string.Join(" | ", problems));
        Assert.Equal(PdfTextExporter.ReadPages(source), PdfTextExporter.ReadPages(result.File.Path));
    }

    [Fact]
    public async Task A_destroyed_page_is_dropped_and_reported()
    {
        var source = Sample("pagina.pdf");
        int pageObject;
        using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Import))
            pageObject = document.Pages[2].Reference!.ObjectID.ObjectNumber;
        var bytes = File.ReadAllBytes(source);
        var latin = Encoding.Latin1.GetString(bytes);
        var start = latin.IndexOf($"\n{pageObject} 0 obj", StringComparison.Ordinal) + 1;
        var end = latin.IndexOf("endobj", start, StringComparison.Ordinal);
        for (var i = start + $"{pageObject} 0 obj".Length; i < end; i++) bytes[i] = (byte)' ';
        "(((".Select(c => (byte)c).ToArray().CopyTo(bytes, start + $"{pageObject} 0 obj".Length + 1);
        var damaged = _temp.File("pagina-danificada.pdf", bytes);

        var result = await _repair.RepairAsync(damaged, null, Target);
        var text = PdfTextExporter.ReadPages(result.File.Path);
        Assert.Equal(3, text.Count);
        Assert.DoesNotContain(text, t => t.Contains("tres", StringComparison.Ordinal));
        Assert.Contains(text, t => t.Contains("quatro", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Garbage_is_unrecoverable()
    {
        var damaged = _temp.File("lixo.pdf", RandomNumberGenerator.GetBytes(4096));
        var report = PdfRepair.Diagnose(damaged);
        Assert.Equal(DamageLevel.Unrecoverable, report.Level);
        Assert.False(report.HasHeader);
        await Assert.ThrowsAsync<PdfEngineException>(() => _repair.RepairAsync(damaged, null, Target));
    }
}

public class PdfATests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly PdfAConverter _converter = new(new OutputPlanner(new OutputPathResolver()));
    private readonly ITestOutputHelper _output;

    public PdfATests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _temp.Dispose();

    private OutputTarget Target => new(Path.Combine(_temp.Path, "saida"), OverwritePolicy.AutoRename, SubfolderMode.None);

    private static bool VeraPdfRequired => Environment.GetEnvironmentVariable("PDFMASTER_REQUIRE_VERAPDF") == "1";

    private async Task<PdfAConversion> Convert(string source, PdfALevel level)
    {
        try
        {
            var result = await _converter.ConvertAsync(source, level, Target);
            foreach (var change in result.Changes) _output.WriteLine("Mudança: " + change);
            _output.WriteLine(result.Validation.Verdict);
            if (VeraPdfRequired) Assert.True(result.Validation.External?.Compliant, result.Validation.External?.Summary ?? "veraPDF não encontrado");
            return result;
        }
        catch (PdfAConversionException ex)
        {
            throw new Xunit.Sdk.XunitException(ex.Message + "\n" + string.Join("\n", ex.Problems));
        }
    }

    [Fact]
    public void VeraPdf_is_available_when_the_build_requires_it()
    {
        var tool = VeraPdf.Find();
        _output.WriteLine(tool ?? "veraPDF não encontrado");
        if (VeraPdfRequired) Assert.NotNull(tool);
    }

    [Theory]
    [InlineData(PdfALevel.A1b)]
    [InlineData(PdfALevel.A2b)]
    [InlineData(PdfALevel.A3b)]
    public async Task Standard_fonts_are_embedded_and_the_text_and_look_are_preserved(PdfALevel level)
    {
        var source = EditorSamples.TextPdf(Path.Combine(_temp.Path, "padrao.pdf"), pages: 2, lines: ["Contrato de compra e venda", "Valor: R$ 1.500,00 — ação"]);
        var before = File.ReadAllBytes(source);
        var result = await Convert(source, level);

        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.EndsWith($"_PDFA-{PdfA.Flavour(level)}.pdf", result.File.Path, StringComparison.Ordinal);
        Assert.True(result.Validation.InternalPassed);
        Assert.Contains(result.Changes, c => c.Contains("Helvetica", StringComparison.Ordinal) && c.Contains("Arial", StringComparison.Ordinal));
        var check = PdfAValidator.Validate(result.File.Path);
        Assert.Equal((int)level, check.DeclaredPart);
        Assert.Equal("B", check.DeclaredConformance);
        Assert.True(check.InternalPassed, string.Join("\n", check.Checks.SelectMany(c => c.Problems)));
        var header = Encoding.ASCII.GetString(File.ReadAllBytes(result.File.Path), 0, 8);
        Assert.Equal(level == PdfALevel.A1b ? "%PDF-1.4" : "%PDF-1.7", header);
        Assert.Equal(PdfTextExporter.ReadPages(source), PdfTextExporter.ReadPages(result.File.Path));

        var renderer = new PdfiumRenderer();
        var a = await renderer.RenderPageAsync(source, 0, RenderSize.ByScale(1));
        var b = await renderer.RenderPageAsync(result.File.Path, 0, RenderSize.ByScale(1));
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        var different = 0;
        for (var i = 0; i < a.Bgra.Length; i += 4)
        {
            if (Math.Abs(a.Bgra[i] - b.Bgra[i]) > 60 || Math.Abs(a.Bgra[i + 1] - b.Bgra[i + 1]) > 60) different++;
        }

        _output.WriteLine($"Pixels diferentes: {different} de {a.Width * a.Height}");
        Assert.True(different < a.Width * a.Height * 0.01);
    }

    [Theory]
    [InlineData(PdfALevel.A1b)]
    [InlineData(PdfALevel.A2b)]
    public async Task Documents_made_by_this_program_convert(PdfALevel level)
    {
        WindowsFonts.EnsureInstalled();
        var source = Path.Combine(_temp.Path, "proprio.pdf");
        using (var document = new PdfDocument())
        {
            var page = document.AddPage();
            using (var graphics = XGraphics.FromPdfPage(page))
            {
                graphics.DrawString("Relatório gerado com acentuação: ção, ê, ü", new XFont("Arial", 14), XBrushes.Black, new XPoint(60, 100));
                graphics.DrawRectangle(XBrushes.SteelBlue, new XRect(60, 140, 200, 80));
            }

            document.Save(source);
        }

        var result = await Convert(source, level);
        Assert.True(result.Validation.InternalPassed);
        Assert.Contains("acentuação", PdfTextExporter.ReadPages(result.File.Path)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transparency_blocks_pdfa1_but_converts_to_pdfa2()
    {
        var source = EditorSamples.TextPdf(Path.Combine(_temp.Path, "transparente.pdf"));
        using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
        {
            using (var graphics = XGraphics.FromPdfPage(document.Pages[0], XGraphicsPdfPageOptions.Append))
                graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(128, 255, 0, 0)), new XRect(60, 300, 200, 100));
            document.Save(source);
        }

        var error = await Assert.ThrowsAsync<PdfAConversionException>(() => _converter.ConvertAsync(source, PdfALevel.A1b, Target));
        Assert.Contains(error.Problems, p => p.Contains("transparência", StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(Target.Folder) && Directory.EnumerateFiles(Target.Folder).Any());
        var result = await Convert(source, PdfALevel.A2b);
        Assert.True(result.Validation.InternalPassed);
    }

    [Fact]
    public async Task Javascript_and_attachments_are_removed_or_identified_by_level()
    {
        var source = EditorSamples.TextPdf(Path.Combine(_temp.Path, "anexos.pdf"));
        using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
        {
            var catalog = document.Internals.Catalog;
            var js = new PdfDictionary(document);
            js.Elements.SetName("/S", "/JavaScript");
            js.Elements.SetString("/JS", "app.alert('oi');");
            catalog.Elements["/OpenAction"] = js;

            var data = new PdfDictionary(document);
            data.CreateStream(Encoding.UTF8.GetBytes("<nota>dados</nota>"));
            document.Internals.AddObject(data);
            var files = new PdfDictionary(document);
            files.Elements.SetReference("/F", data);
            var spec = new PdfDictionary(document);
            spec.Elements.SetName("/Type", "/Filespec");
            spec.Elements.SetString("/F", "dados.xml");
            spec.Elements["/EF"] = files;
            document.Internals.AddObject(spec);
            var pairs = new PdfArray(document);
            pairs.Elements.Add(new PdfString("dados.xml"));
            pairs.Elements.Add(spec.Reference!);
            var tree = new PdfDictionary(document);
            tree.Elements["/Names"] = pairs;
            var names = new PdfDictionary(document);
            names.Elements["/EmbeddedFiles"] = tree;
            catalog.Elements["/Names"] = names;
            document.Save(source);
        }

        var two = await Convert(source, PdfALevel.A2b);
        Assert.Contains(two.Changes, c => c.Contains("ação(ões) proibida(s)", StringComparison.Ordinal));
        Assert.Contains(two.Changes, c => c.Contains("anexados", StringComparison.Ordinal));
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(two.File.Path));
        Assert.DoesNotContain("/JavaScript", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/EmbeddedFiles", text, StringComparison.Ordinal);

        var three = await Convert(source, PdfALevel.A3b);
        Assert.Contains(three.Changes, c => c.Contains("anexo(s) identificado(s)", StringComparison.Ordinal));
        using var converted = PdfReader.Open(three.File.Path, PdfDocumentOpenMode.Import);
        Assert.NotNull(converted.Internals.Catalog.Elements.GetArray("/AF"));
        Assert.NotNull(converted.Internals.Catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/EmbeddedFiles"));
    }

    [Fact]
    public async Task Cmyk_colors_without_a_matching_profile_stop_the_conversion()
    {
        var source = EditorSamples.TextPdf(Path.Combine(_temp.Path, "cmyk.pdf"));
        using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
        {
            document.Pages[0].Contents.AppendContent().CreateStream("0 0 0 1 k 72 500 200 60 re f\n"u8.ToArray());
            document.Save(source);
        }

        var error = await Assert.ThrowsAsync<PdfAConversionException>(() => _converter.ConvertAsync(source, PdfALevel.A2b, Target));
        Assert.Contains(error.Problems, p => p.Contains("CMYK", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Protected_documents_must_be_unlocked_first()
    {
        var source = EditorSamples.TextPdf(Path.Combine(_temp.Path, "senha.pdf"));
        var locked = await new PdfProtection(new OutputPlanner(new OutputPathResolver()))
            .ProtectAsync(source, new ProtectOptions("abrir123", null, PdfPermissions.All), Target);
        var error = await Assert.ThrowsAsync<PdfEngineException>(() => _converter.ConvertAsync(locked.Path, PdfALevel.A2b, Target));
        Assert.Contains("proteção", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_pdf_fails_validation_with_explanations()
    {
        var source = EditorSamples.TextPdf(Path.Combine(_temp.Path, "comum.pdf"));
        var validation = PdfAValidator.Validate(source, PdfALevel.A2b);
        Assert.False(validation.InternalPassed);
        Assert.Equal(0, validation.DeclaredPart);
        Assert.Equal(CheckStatus.Failed, validation.Checks.Single(c => c.Id == "metadados").Status);
        Assert.Equal(CheckStatus.Failed, validation.Checks.Single(c => c.Id == "fontes").Status);
        Assert.Equal(CheckStatus.Failed, validation.Checks.Single(c => c.Id == "cores").Status);
        Assert.StartsWith("NÃO conforme", validation.Verdict, StringComparison.Ordinal);
    }
}

/// <summary>Documentos da Fase 6 para o teste de inicialização do programa (gerados só quando PDFMASTER_SMOKE_DIR existe).</summary>
public class SecuritySmokeSamples
{
    [Fact]
    public async Task Writes_phase_six_sample_documents()
    {
        var folder = Environment.GetEnvironmentVariable("PDFMASTER_SMOKE_DIR");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);

        SecuritySamples.Pages(Path.Combine(folder, "Contrato v1.pdf"),
            ["CONTRATO DE COMPRA E VENDA", "O vendedor transfere o imovel ao comprador.", "Preco: R$ 300.000,00", "Pagamento em 10 parcelas."],
            ["Clausula segunda: entrega das chaves em 30 dias.", "Clausula terceira: foro da comarca."]);
        SecuritySamples.Pages(Path.Combine(folder, "Contrato v2.pdf"),
            ["CONTRATO DE COMPRA E VENDA", "O vendedor transfere o imovel ao comprador.", "Preco: R$ 320.000,00", "Pagamento a vista."],
            ["Anexo I: memorial descritivo do imovel."],
            ["Clausula segunda: entrega das chaves em 45 dias.", "Clausula terceira: foro da comarca."]);

        using (var certificate = SecuritySamples.SelfSigned("Signatário de Exemplo", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1)))
            File.WriteAllBytes(Path.Combine(folder, "Assinado.pdf"), SecuritySamples.Signed(certificate, DateTimeOffset.UtcNow));

        var source = SecuritySamples.Pages(Path.Combine(folder, "fonte-danificado.pdf"), ["Documento que sera danificado"], ["Segunda pagina"]);
        var bytes = File.ReadAllBytes(source);
        var header = Array.IndexOf(bytes, (byte)'\n') + 1;
        File.WriteAllBytes(Path.Combine(folder, "Danificado.pdf"), [.. bytes[..header], .. Encoding.ASCII.GetBytes("% bytes a mais deslocam a tabela\n"), .. bytes[header..]]);
        File.Delete(source);

        var temp = Path.Combine(folder, "protecao");
        var produced = await new PdfProtection(new OutputPlanner(new OutputPathResolver())).ProtectAsync(
            Path.Combine(folder, "Contrato v1.pdf"), new ProtectOptions(null, "dono1234", PdfPermissions.All with { Copy = false, Modify = false }),
            new OutputTarget(temp, OverwritePolicy.Overwrite, SubfolderMode.None));
        File.Move(produced.Path, Path.Combine(folder, "Protegido.pdf"), overwrite: true);
        Directory.Delete(temp, recursive: true);
    }
}
