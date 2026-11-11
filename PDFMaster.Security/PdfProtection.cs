using System.Reflection;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;
using PDFMaster.PdfEngine;
using PDFMaster.Rendering;

namespace PDFMaster.Security;

/// <summary>Permissões de um PDF protegido (bits /P).</summary>
public sealed record PdfPermissions(bool Print, bool HighQualityPrint, bool Copy, bool Modify, bool Annotate, bool FillForms, bool Assemble, bool Accessibility)
{
    public static readonly PdfPermissions All = new(true, true, true, true, true, true, true, true);

    public static PdfPermissions FromBits(uint p) => new(
        (p & 4) != 0, (p & 2048) != 0, (p & 16) != 0, (p & 8) != 0, (p & 32) != 0, (p & 256) != 0, (p & 1024) != 0, (p & 512) != 0);

    public IEnumerable<string> Denied()
    {
        if (!Print) yield return "imprimir";
        else if (!HighQualityPrint) yield return "imprimir em alta qualidade";
        if (!Copy) yield return "copiar texto e imagens";
        if (!Modify) yield return "alterar o documento";
        if (!Annotate) yield return "comentar";
        if (!FillForms) yield return "preencher formulários";
        if (!Assemble) yield return "montar (inserir, girar, excluir páginas)";
        if (!Accessibility) yield return "extrair para acessibilidade";
    }
}

/// <summary>Propriedades de segurança de um PDF.</summary>
/// <param name="PasswordRequired">Precisa de senha para abrir.</param>
/// <param name="Encryption">Descrição do algoritmo (ex.: "AES de 256 bits"), ou null sem criptografia.</param>
/// <param name="Permissions">Permissões de quem abre sem a senha de proprietário (null se precisar de senha para saber).</param>
public sealed record SecurityReport(
    bool Opened, bool PasswordRequired, bool Encrypted, string? Encryption, PdfPermissions? Permissions,
    int PageCount, string? Version, IReadOnlyList<int> BrokenPages, string? Problem)
{
    public bool HasRestrictions => Permissions is not null && Permissions != PdfPermissions.All && Encrypted;
}

/// <summary>Opções de proteção.</summary>
/// <param name="UserPassword">Senha de abertura (vazia = abre sem senha, só com as restrições).</param>
/// <param name="OwnerPassword">Senha de proprietário (dá todas as permissões); vazia = igual à de abertura.</param>
public sealed record ProtectOptions(string? UserPassword, string? OwnerPassword, PdfPermissions Allowed);

/// <summary>
/// Proteção por senha (AES de 256 bits quando disponível no PDFsharp), remoção de proteção com a senha
/// fornecida pela pessoa e inspeção das propriedades de segurança. Senhas nunca são gravadas.
/// </summary>
public sealed class PdfProtection
{
    public const string ProtectOperation = "Proteger com senha";
    public const string UnlockOperation = "Remover proteção";
    public const int MinPasswordLength = 4;

    private readonly OutputPlanner _planner;

    public PdfProtection(OutputPlanner planner) => _planner = planner;

    public static string? EncryptionName(int revision) => revision switch
    {
        < 0 => null,
        2 => "RC4 de 40 bits (fraca)",
        3 => "RC4 de 128 bits",
        4 => "AES ou RC4 de 128 bits",
        5 or 6 => "AES de 256 bits",
        _ => $"revisão {revision} do manipulador de segurança",
    };

    /// <summary>
    /// Algoritmo pelo filtro de criptografia declarado (/CFM do dicionário /Encrypt, que fica sem
    /// criptografia no arquivo); sem ele, pela revisão do manipulador de segurança.
    /// </summary>
    private static string? Algorithm(string path, int revision)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(stream.Length, 64L * 1024 * 1024);
            var buffer = new byte[length];
            stream.ReadExactly(buffer);
            var text = System.Text.Encoding.Latin1.GetString(buffer);
            if (text.Contains("/AESV3", StringComparison.Ordinal)) return "AES de 256 bits";
            if (text.Contains("/AESV2", StringComparison.Ordinal)) return "AES de 128 bits";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sem acesso ao arquivo: fica a descrição pela revisão.
        }

        return revision == 4 ? "RC4 de 128 bits" : EncryptionName(revision);
    }

    public static SecurityReport Inspect(string path, string? password = null)
    {
        if (!File.Exists(path)) throw new PdfEngineException($"Arquivo não encontrado: \"{path}\".");
        var pdfium = PdfiumRenderer.Inspect(path, password, checkPages: true);
        if (!pdfium.Opened)
        {
            var problem = pdfium.PasswordRequired
                ? password is null ? "O documento pede senha para abrir." : "Senha incorreta."
                : pdfium.Error == 3 ? "O arquivo não é um PDF ou está muito danificado." : "O arquivo não pôde ser aberto.";
            return new SecurityReport(false, pdfium.PasswordRequired, pdfium.PasswordRequired, null, null, 0, null, [], problem);
        }

        var version = pdfium.FileVersion > 0 ? $"{pdfium.FileVersion / 10}.{pdfium.FileVersion % 10}" : null;
        var encrypted = pdfium.SecurityRevision >= 0;
        return new SecurityReport(true, password is not null && encrypted, encrypted, encrypted ? Algorithm(path, pdfium.SecurityRevision) : null,
            encrypted ? PdfPermissions.FromBits(pdfium.Permissions) : PdfPermissions.All, pdfium.PageCount, version, pdfium.BrokenPages, null);
    }

    public Task<ProducedFile> ProtectAsync(string source, ProtectOptions options, OutputTarget target, CancellationToken ct = default, ISet<string>? reserved = null)
    {
        var user = options.UserPassword ?? string.Empty;
        var owner = string.IsNullOrEmpty(options.OwnerPassword) ? user : options.OwnerPassword;
        if (owner.Length < MinPasswordLength)
            throw new PdfEngineException($"Informe uma senha de proprietário (ou de abertura) com pelo menos {MinPasswordLength} caracteres.");
        if (user.Length > 0 && user.Length < MinPasswordLength)
            throw new PdfEngineException($"A senha de abertura deve ter pelo menos {MinPasswordLength} caracteres.");
        if (user.Length == 0 && options.Allowed == PdfPermissions.All)
            throw new PdfEngineException("Sem senha de abertura e sem nenhuma restrição, a proteção não teria efeito.");

        var full = Path.GetFullPath(source);
        if (PdfiumRenderer.Inspect(full).Encrypted)
            throw new PdfEngineException("O documento já está protegido. Remova a proteção antes de aplicar uma nova.");
        var (output, overwrite) = _planner.Resolve(Path.GetFileNameWithoutExtension(full) + "_protegido.pdf", target, ProtectOperation, [full], reserved);
        return Task.Run(() =>
        {
            using var document = Open(full, null);
            var settings = document.SecuritySettings;
            settings.UserPassword = user;
            settings.OwnerPassword = owner;
            var a = options.Allowed;
            settings.PermitPrint = a.Print;
            settings.PermitFullQualityPrint = a.Print && a.HighQualityPrint;
            settings.PermitExtractContent = a.Copy;
            settings.PermitModifyDocument = a.Modify;
            settings.PermitAnnotations = a.Annotate;
            settings.PermitFormsFill = a.FillForms;
            settings.PermitAssembleDocument = a.Assemble;
            SetPermission(settings, a.Accessibility, "PermitAccessibilityExtractContent", "PermitExtractContentForAccessibility", "PermitAccessibility");
            UseStrongestEncryption(document);
            ct.ThrowIfCancellationRequested();
            var pages = document.PageCount;
            var bytes = OutputPlanner.WriteAtomically(output, overwrite, document.Save);
            return new ProducedFile(output, bytes, pages);
        }, ct);
    }

    /// <summary>Remove senha e restrições. Exige a senha de proprietário (ou uma senha que dê todas as permissões).</summary>
    public Task<ProducedFile> UnlockAsync(string source, string password, OutputTarget target, CancellationToken ct = default, ISet<string>? reserved = null)
    {
        var full = Path.GetFullPath(source);
        var check = PdfiumRenderer.Inspect(full, password);
        if (!check.Opened)
            throw new PdfEngineException(check.PasswordRequired ? "Senha incorreta." : "O documento não pôde ser aberto.");
        if (check.SecurityRevision < 0) throw new PdfEngineException("Este documento não está protegido.");
        if (PdfPermissions.FromBits(check.Permissions) != PdfPermissions.All)
            throw new PdfEngineException("Esta senha abre o documento, mas não é a senha de proprietário: ela não autoriza remover as restrições.");

        var (output, overwrite) = _planner.Resolve(Path.GetFileNameWithoutExtension(full) + "_desprotegido.pdf", target, UnlockOperation, [full], reserved);
        return Task.Run(() =>
        {
            using var document = Open(full, password);
            RemoveEncryption(document);
            ct.ThrowIfCancellationRequested();
            var pages = document.PageCount;
            var bytes = OutputPlanner.WriteAtomically(output, overwrite, temp =>
            {
                document.Save(temp);
                if (PdfiumRenderer.Inspect(temp).SecurityRevision >= 0)
                    throw new PdfEngineException("A proteção não pôde ser removida por esta versão do mecanismo de PDF.");
            });
            return new ProducedFile(output, bytes, pages);
        }, ct);
    }

    internal static PdfDocument Open(string path, string? password)
    {
        try
        {
            return password is null
                ? PdfReader.Open(path, PdfDocumentOpenMode.Modify)
                : PdfReader.Open(path, password, PdfDocumentOpenMode.Modify);
        }
        catch (PdfReaderException ex)
        {
            throw new PdfEngineException(password is null
                ? $"\"{Path.GetFileName(path)}\" não pôde ser aberto: pode estar protegido por senha ou danificado."
                : "A senha não foi aceita ou o documento está danificado.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PdfEngineException($"Não foi possível abrir \"{Path.GetFileName(path)}\": {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException and not PdfEngineException)
        {
            throw new PdfEngineException($"\"{Path.GetFileName(path)}\" não pôde ser interpretado como PDF: {ex.Message}", ex);
        }
    }

    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Permissões cujo nome varia entre versões do PDFsharp.</summary>
    private static void SetPermission(PdfSecuritySettings settings, bool value, params string[] names)
    {
        var property = names.Select(n => settings.GetType().GetProperty(n, BindingFlags.Instance | BindingFlags.Public)).FirstOrDefault(p => p is { CanWrite: true })
                       ?? settings.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                           .FirstOrDefault(p => p.CanWrite && p.PropertyType == typeof(bool) && p.Name.Contains("Accessibility", StringComparison.Ordinal));
        property?.SetValue(settings, value);
    }

    /// <summary>AES de 256 bits (revisão 6) quando a versão do PDFsharp oferece; senão, o padrão dela.</summary>
    private static void UseStrongestEncryption(PdfDocument document)
    {
        var handler = document.GetType().GetProperty("SecurityHandler", Members)?.GetValue(document);
        if (handler is null) return;
        foreach (var name in new[] { "SetEncryptionToV5", "SetEncryptionToV4UsingAES" })
        {
            var method = handler.GetType().GetMethod(name, Members, Type.EmptyTypes);
            if (method is null) continue;
            method.Invoke(handler, null);
            return;
        }

        var setter = handler.GetType().GetMethods(Members).FirstOrDefault(m => m.Name == "SetEncryption" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
        if (setter is null) return;
        var enumType = setter.GetParameters()[0].ParameterType;
        var value = Enum.GetNames(enumType).FirstOrDefault(n => n.Contains("V5", StringComparison.Ordinal))
                    ?? Enum.GetNames(enumType).FirstOrDefault(n => n.Contains("AES", StringComparison.OrdinalIgnoreCase));
        if (value is not null) setter.Invoke(handler, [Enum.Parse(enumType, value)]);
    }

    private static void RemoveEncryption(PdfDocument document)
    {
        var handler = document.GetType().GetProperty("SecurityHandler", Members)?.GetValue(document);
        var setter = handler?.GetType().GetMethods(Members).FirstOrDefault(m => m.Name == "SetEncryption" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum);
        if (setter is not null)
        {
            var enumType = setter.GetParameters()[0].ParameterType;
            if (Enum.GetNames(enumType).Contains("None"))
            {
                setter.Invoke(handler, [Enum.Parse(enumType, "None")]);
                return;
            }
        }

        var settings = document.SecuritySettings;
        var level = settings.GetType().GetProperty("DocumentSecurityLevel", Members);
        if (level is not null && level.CanWrite && Enum.GetNames(level.PropertyType).Contains("None"))
        {
            level.SetValue(settings, Enum.Parse(level.PropertyType, "None"));
            return;
        }

        settings.UserPassword = string.Empty;
        settings.OwnerPassword = string.Empty;
    }
}
