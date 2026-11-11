using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PDFMaster.PdfEngine;

namespace PDFMaster.Security;

public enum SignatureIntegrity
{
    /// <summary>A assinatura confere com o conteúdo assinado e cobre o documento inteiro.</summary>
    Intact,

    /// <summary>A assinatura confere, mas o arquivo recebeu conteúdo depois dela (atualização incremental).</summary>
    IntactButModifiedLater,

    /// <summary>O conteúdo assinado foi alterado: a assinatura não confere.</summary>
    Broken,

    /// <summary>Formato não suportado ou dados da assinatura ilegíveis.</summary>
    Unverifiable,
}

public enum CertificateTrust
{
    /// <summary>Cadeia completa até uma raiz confiável do Windows (ou da pasta de raízes do programa).</summary>
    Trusted,

    /// <summary>Cadeia montada, mas a raiz não é confiável neste computador.</summary>
    UntrustedRoot,

    /// <summary>Cadeia incompleta ou com problemas (expirada na data da assinatura, uso indevido etc.).</summary>
    Invalid,

    Unknown,
}

/// <summary>Resultado da verificação de uma assinatura.</summary>
/// <param name="CoversWholeDocument">A faixa assinada vai até o fim do arquivo.</param>
/// <param name="BytesAfterSignature">Bytes acrescentados depois da faixa assinada.</param>
/// <param name="Chain">Certificados da cadeia, do signatário à raiz.</param>
public sealed record SignatureReport(
    string FieldName, int PageIndex, string? SubFilter,
    string? SignerName, string? SignerSubject, string? Issuer, string? SerialNumber,
    DateTimeOffset? NotBefore, DateTimeOffset? NotAfter, DateTimeOffset? SigningTime, bool HasTimestamp, bool TimestampValid,
    string? Reason, string? Location, string? DigestAlgorithm,
    SignatureIntegrity Integrity, bool CoversWholeDocument, long BytesAfterSignature,
    bool CertificateValidAtSigningTime, CertificateTrust Trust, IReadOnlyList<string> Chain, bool IcpBrasil,
    IReadOnlyList<string> Notes)
{
    /// <summary>Conclusão em uma frase, sem exagerar o que foi verificado.</summary>
    public string Verdict => Integrity switch
    {
        SignatureIntegrity.Broken => "Assinatura INVÁLIDA: o conteúdo assinado foi alterado.",
        SignatureIntegrity.Unverifiable => "Não foi possível verificar esta assinatura.",
        _ => (Integrity == SignatureIntegrity.Intact ? "Assinatura íntegra: o documento não mudou desde a assinatura. " : "Assinatura íntegra, mas o documento recebeu alterações depois dela. ")
             + Trust switch
             {
                 CertificateTrust.Trusted => "Certificado com cadeia confiável neste computador.",
                 CertificateTrust.UntrustedRoot => "A identidade do signatário não pôde ser confirmada: a raiz do certificado não é confiável neste computador.",
                 CertificateTrust.Invalid => "O certificado tem problemas (veja os detalhes).",
                 _ => "A cadeia do certificado não pôde ser verificada.",
             },
    };
}

/// <summary>
/// Verificação criptográfica de assinaturas digitais (PKCS#7/CMS: adbe.pkcs7.detached,
/// ETSI.CAdES.detached e adbe.pkcs7.sha1). Confere o resumo do conteúdo assinado, a assinatura do
/// signatário, a validade do certificado na data da assinatura e a cadeia de certificação, sem rede
/// (sem consulta de revogação). A presença de um certificado nunca basta para declarar validade.
/// </summary>
public static class SignatureVerifier
{
    private const string IcpBrasilRoot = "ICP-Brasil";

    /// <summary>Verifica todas as assinaturas. <paramref name="extraRoots"/>: raízes adicionais confiáveis (ex.: ICP-Brasil).</summary>
    public static IReadOnlyList<SignatureReport> Verify(string path, IReadOnlyCollection<X509Certificate2>? extraRoots = null)
    {
        byte[] file;
        PdfDocument document;
        try
        {
            file = File.ReadAllBytes(path);
            document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        }
        catch (PdfReaderException ex)
        {
            throw new PdfEngineException($"\"{Path.GetFileName(path)}\" não pôde ser lido (protegido por senha ou danificado).", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfEngineException($"Não foi possível abrir \"{Path.GetFileName(path)}\": {ex.Message}", ex);
        }

        using (document)
        {
            var reports = new List<SignatureReport>();
            foreach (var (name, signature, page) in SignatureFields(document))
            {
                reports.Add(VerifyOne(file, name, signature, page, extraRoots));
            }

            return reports;
        }
    }

    /// <summary>Quantidade de campos de assinatura preenchidos (sem verificar).</summary>
    public static int Count(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return SignatureFields(document).Count();
    }

    private static IEnumerable<(string Name, PdfDictionary Signature, int Page)> SignatureFields(PdfDocument document)
    {
        var fields = document.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields");
        if (fields is null) yield break;

        var pages = new Dictionary<int, int>();
        for (var p = 0; p < document.PageCount; p++)
        {
            var annots = document.Pages[p].Elements.GetArray("/Annots");
            if (annots is null) continue;
            for (var i = 0; i < annots.Elements.Count; i++)
            {
                if (annots.Elements[i] is PdfReference r) pages.TryAdd(r.ObjectID.ObjectNumber, p);
            }
        }

        var stack = new Stack<(PdfDictionary Node, string Prefix, string? Type)>();
        for (var i = fields.Elements.Count - 1; i >= 0; i--)
        {
            if (fields.Elements.GetDictionary(i) is { } f) stack.Push((f, string.Empty, null));
        }

        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        while (stack.Count > 0)
        {
            var (node, prefix, inherited) = stack.Pop();
            if (!seen.Add(node)) continue;
            var partial = node.Elements.GetString("/T");
            var name = partial.Length == 0 ? prefix : prefix.Length == 0 ? partial : prefix + "." + partial;
            var type = node.Elements.ContainsKey("/FT") ? node.Elements.GetName("/FT") : inherited;
            var kids = node.Elements.GetArray("/Kids");
            if (kids is not null && Enumerable.Range(0, kids.Elements.Count).Any(i => kids.Elements.GetDictionary(i)?.Elements.ContainsKey("/T") == true))
            {
                for (var i = kids.Elements.Count - 1; i >= 0; i--)
                {
                    if (kids.Elements.GetDictionary(i) is { } kid) stack.Push((kid, name, type));
                }

                continue;
            }

            if (type != "/Sig" || node.Elements.GetDictionary("/V") is not { } value) continue;
            var widget = kids is { Elements.Count: > 0 } ? kids.Elements.GetDictionary(0) ?? node : node;
            var page = widget.Reference is { } wr && pages.TryGetValue(wr.ObjectID.ObjectNumber, out var p2) ? p2 : -1;
            yield return (name.Length == 0 ? "(assinatura sem nome)" : name, value, page);
        }
    }

    private static SignatureReport VerifyOne(byte[] file, string name, PdfDictionary signature, int page, IReadOnlyCollection<X509Certificate2>? extraRoots)
    {
        var notes = new List<string>();
        var subFilter = signature.Elements.GetName("/SubFilter").TrimStart('/');
        var reason = Text(signature, "/Reason");
        var location = Text(signature, "/Location");
        var declaredName = Text(signature, "/Name");
        DateTimeOffset? declaredTime = signature.Elements.ContainsKey("/M") && signature.Elements.GetDateTime("/M", DateTime.MinValue) is var m && m != DateTime.MinValue ? m : null;

        SignatureReport Fail(string note, SignatureIntegrity integrity = SignatureIntegrity.Unverifiable)
        {
            notes.Add(note);
            return new SignatureReport(name, page, subFilter, declaredName, null, null, null, null, null, declaredTime, false, false,
                reason, location, null, integrity, false, 0, false, CertificateTrust.Unknown, [], false, notes);
        }

        // Faixa assinada: [início1 tamanho1 início2 tamanho2]; o intervalo entre elas guarda a assinatura em hexadecimal.
        var range = signature.Elements.GetArray("/ByteRange");
        if (range is null || range.Elements.Count != 4) return Fail("A assinatura não informa a faixa de bytes assinada (/ByteRange).");
        long[] r = Enumerable.Range(0, 4).Select(i => (long)range.Elements.GetInteger(i)).ToArray();
        if (r[0] != 0 || r[1] <= 0 || r[2] <= r[1] || r[3] < 0 || r[2] + r[3] > file.LongLength)
            return Fail("A faixa de bytes assinada é inválida ou ultrapassa o tamanho do arquivo.", SignatureIntegrity.Broken);

        var gap = Encoding.ASCII.GetString(file, (int)r[1], (int)(r[2] - r[1])).Trim();
        if (!gap.StartsWith('<') || !gap.EndsWith('>'))
            return Fail("O espaço reservado para a assinatura não contém dados hexadecimais.", SignatureIntegrity.Broken);
        byte[] contents;
        try
        {
            contents = Convert.FromHexString(gap[1..^1].Trim());
        }
        catch (FormatException)
        {
            return Fail("Os dados da assinatura não são hexadecimais válidos.", SignatureIntegrity.Broken);
        }

        contents = TrimDer(contents);
        var signed = new byte[r[1] + r[3]];
        Buffer.BlockCopy(file, 0, signed, 0, (int)r[1]);
        Buffer.BlockCopy(file, (int)r[2], signed, (int)r[1], (int)r[3]);
        var after = file.LongLength - (r[2] + r[3]);

        SignedCms cms;
        var integrity = SignatureIntegrity.Unverifiable;
        try
        {
            switch (subFilter)
            {
                case "adbe.pkcs7.detached" or "ETSI.CAdES.detached":
                    cms = new SignedCms(new ContentInfo(signed), detached: true);
                    cms.Decode(contents);
                    break;
                case "adbe.pkcs7.sha1":
                    cms = new SignedCms();
                    cms.Decode(contents);
                    if (!cms.ContentInfo.Content.AsSpan().SequenceEqual(SHA1.HashData(signed)))
                        return Fail("O resumo SHA-1 guardado na assinatura não confere com o documento.", SignatureIntegrity.Broken);
                    break;
                default:
                    return Fail($"Formato de assinatura não suportado ({(subFilter.Length == 0 ? "sem /SubFilter" : subFilter)}).");
            }
        }
        catch (CryptographicException ex)
        {
            return Fail("Os dados da assinatura não puderam ser lidos: " + ex.Message);
        }

        if (cms.SignerInfos.Count == 0) return Fail("A assinatura não tem informações do signatário.");
        var signer = cms.SignerInfos[0];
        try
        {
            signer.CheckSignature(verifySignatureOnly: true);
            integrity = after > 0 ? SignatureIntegrity.IntactButModifiedLater : SignatureIntegrity.Intact;
        }
        catch (CryptographicException)
        {
            integrity = SignatureIntegrity.Broken;
            notes.Add("O resumo criptográfico não confere: o conteúdo coberto pela assinatura foi modificado depois de assinado.");
        }

        if (after > 0)
            notes.Add($"Há {after} bytes acrescentados depois da assinatura (outra assinatura, preenchimento de campos ou alterações). O conteúdo novo não é coberto por esta assinatura.");

        var certificate = signer.Certificate;
        DateTimeOffset? signingTime = declaredTime;
        foreach (var attribute in signer.SignedAttributes)
        {
            if (attribute.Oid.Value == "1.2.840.113549.1.9.5" && attribute.Values.Count > 0)
                signingTime = new Pkcs9SigningTime(attribute.Values[0].RawData).SigningTime;
        }

        // Carimbo do tempo (RFC 3161) como atributo não assinado.
        var hasTimestamp = false;
        var timestampValid = false;
        foreach (var attribute in signer.UnsignedAttributes)
        {
            if (attribute.Oid.Value != "1.2.840.113549.1.9.16.2.14" || attribute.Values.Count == 0) continue;
            hasTimestamp = true;
            if (Rfc3161TimestampToken.TryDecode(attribute.Values[0].RawData, out var token, out _) &&
                token.VerifySignatureForSignerInfo(signer, out _))
            {
                timestampValid = true;
                signingTime = token.TokenInfo.Timestamp;
            }
            else
            {
                notes.Add("O carimbo do tempo existe, mas não pôde ser validado.");
            }
        }

        if (certificate is null)
            return new SignatureReport(name, page, subFilter, declaredName, null, null, null, null, null, signingTime, hasTimestamp, timestampValid,
                reason, location, signer.DigestAlgorithm.FriendlyName, integrity, after == 0, after, false, CertificateTrust.Unknown, [], false,
                [.. notes, "O certificado do signatário não está incluído na assinatura."]);

        var when = signingTime ?? DateTimeOffset.Now;
        var validAtSigning = when >= certificate.NotBefore && when <= certificate.NotAfter;
        if (!validAtSigning) notes.Add("O certificado não estava dentro do prazo de validade na data da assinatura.");
        if (signingTime is null) notes.Add("A assinatura não informa a data; a validade do certificado foi conferida com a data de hoje.");
        else if (!hasTimestamp) notes.Add("A data da assinatura foi declarada pelo próprio signatário (não há carimbo do tempo).");

        var (trust, chain, icp) = BuildChain(certificate, cms.Certificates, when, extraRoots, notes);
        if (icp) notes.Add("A cadeia termina numa autoridade da ICP-Brasil. A validação completa ICP-Brasil (raízes oficiais, revogação, políticas) ainda não é feita por este programa.");
        notes.Add("A revogação do certificado não foi consultada (o programa não acessa a rede).");

        return new SignatureReport(name, page, subFilter,
            certificate.GetNameInfo(X509NameType.SimpleName, false), certificate.Subject, certificate.Issuer, certificate.SerialNumber,
            certificate.NotBefore, certificate.NotAfter, signingTime, hasTimestamp, timestampValid,
            reason, location, signer.DigestAlgorithm.FriendlyName ?? signer.DigestAlgorithm.Value,
            integrity, after == 0, after, validAtSigning, trust, chain, icp, notes);
    }

    private static (CertificateTrust Trust, IReadOnlyList<string> Chain, bool IcpBrasil) BuildChain(
        X509Certificate2 certificate, X509Certificate2Collection included, DateTimeOffset when, IReadOnlyCollection<X509Certificate2>? extraRoots, List<string> notes)
    {
        CertificateTrust Evaluate(X509Chain chain, out string[] names)
        {
            var ok = chain.Build(certificate);
            names = chain.ChainElements.Select(e => e.Certificate.GetNameInfo(X509NameType.SimpleName, false)).ToArray();
            if (ok) return CertificateTrust.Trusted;
            var flags = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (a, s) => a | s.Status);
            if ((flags & ~(X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain | X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) == 0)
                return CertificateTrust.UntrustedRoot;
            foreach (var status in chain.ChainStatus.Where(s => s.Status is not (X509ChainStatusFlags.UntrustedRoot or X509ChainStatusFlags.PartialChain)))
                notes.Add("Cadeia do certificado: " + status.StatusInformation.Trim());
            return CertificateTrust.Invalid;
        }

        X509Chain NewChain()
        {
            var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationTime = when.LocalDateTime;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ExtraStore.AddRange(included);
            return chain;
        }

        using var system = NewChain();
        var trust = Evaluate(system, out var names);
        if (trust != CertificateTrust.Trusted && extraRoots is { Count: > 0 })
        {
            using var custom = NewChain();
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            custom.ChainPolicy.CustomTrustStore.AddRange(extraRoots.ToArray());
            var customTrust = Evaluate(custom, out var customNames);
            if (customTrust == CertificateTrust.Trusted)
            {
                notes.Add("Cadeia confiável pelas raízes adicionadas na pasta de certificados do programa.");
                (trust, names) = (customTrust, customNames);
            }
        }

        var icp = names.Any(n => n.Contains(IcpBrasilRoot, StringComparison.OrdinalIgnoreCase) || n.Contains("Raiz Brasileira", StringComparison.OrdinalIgnoreCase));
        return (trust, names, icp);
    }

    /// <summary>O espaço reservado vem completado com zeros; o CMS termina onde o comprimento DER indica.</summary>
    private static byte[] TrimDer(byte[] data)
    {
        try
        {
            var reader = new AsnReader(data, AsnEncodingRules.BER);
            var encoded = reader.ReadEncodedValue();
            return encoded.ToArray();
        }
        catch (AsnContentException)
        {
            return data;
        }
    }

    private static string? Text(PdfDictionary dictionary, string key)
    {
        var value = dictionary.Elements.GetString(key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
