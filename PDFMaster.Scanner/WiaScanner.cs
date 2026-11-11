using System.Globalization;
using System.Runtime.InteropServices;

namespace PDFMaster.Scanner;

public sealed record ScannerDevice(string Id, string Name);

public enum ScanSource
{
    /// <summary>Vidro do scanner (mesa): uma página por digitalização.</summary>
    Flatbed,
    /// <summary>Alimentador automático: todas as folhas da bandeja.</summary>
    Feeder,
    /// <summary>Alimentador automático, frente e verso (quando o aparelho suporta).</summary>
    FeederDuplex,
}

public enum ScanColor
{
    Color,
    Grayscale,
    BlackAndWhite,
}

public sealed record ScanSettings(ScanSource Source = ScanSource.Flatbed, ScanColor Color = ScanColor.Color, int Dpi = 300);

/// <summary>Erro de digitalização, já com mensagem em português para o usuário.</summary>
public sealed class ScannerException : Exception
{
    public ScannerException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// Digitalização pelo WIA (Windows Image Acquisition), a interface de scanners do próprio Windows.
/// Usa a automação COM "WIA.DeviceManager", presente no Windows 10 e 11; nenhum componente extra
/// é instalado. Scanners que só têm driver TWAIN não aparecem aqui.
/// </summary>
public sealed class WiaScanner
{
    private const int DeviceTypeScanner = 1;
    private const string FormatBmp = "{B96B3CAB-0728-11D3-9D7B-0000F81EF32E}";

    // Identificadores de propriedades do WIA.
    private const int PropDocumentHandlingSelect = 3088;
    private const int PropPages = 3096;
    private const int PropCurrentIntent = 6146;
    private const int PropHorizontalResolution = 6147;
    private const int PropVerticalResolution = 6148;

    private const int ErrorPaperEmpty = unchecked((int)0x80210003);

    public Task<IReadOnlyList<ScannerDevice>> ListDevicesAsync() => RunSta<IReadOnlyList<ScannerDevice>>(() =>
    {
        var devices = new List<ScannerDevice>();
        dynamic manager = CreateManager();
        foreach (dynamic info in manager.DeviceInfos)
        {
            if ((int)info.Type != DeviceTypeScanner) continue;
            string id = info.DeviceID;
            var name = id;
            try
            {
                name = Convert.ToString(info.Properties["Name"].Value, CultureInfo.CurrentCulture) ?? id;
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException
                                           or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // Sem nome legível, o identificador é exibido.
            }

            devices.Add(new ScannerDevice(id, name));
        }

        return devices;
    });

    /// <summary>
    /// Digitaliza e grava cada página como BMP em <paramref name="outputFolder"/>. Devolve os
    /// arquivos na ordem em que foram digitalizados. O cancelamento vale entre uma página e outra.
    /// </summary>
    public Task<IReadOnlyList<string>> ScanAsync(
        string deviceId,
        ScanSettings settings,
        string outputFolder,
        IProgress<int>? pagesScanned = null,
        CancellationToken ct = default) => RunSta<IReadOnlyList<string>>(() =>
    {
        if (settings.Dpi is < 50 or > 1200) throw new ScannerException("A resolução deve ficar entre 50 e 1200 DPI.");
        Directory.CreateDirectory(outputFolder);

        var files = new List<string>();
        try
        {
            dynamic manager = CreateManager();
            dynamic? device = null;
            foreach (dynamic info in manager.DeviceInfos)
            {
                if ((int)info.Type == DeviceTypeScanner && string.Equals((string)info.DeviceID, deviceId, StringComparison.Ordinal))
                {
                    device = info.Connect();
                    break;
                }
            }

            if (device is null)
                throw new ScannerException("O scanner selecionado não foi encontrado. Verifique se está ligado e conectado e atualize a lista.");

            var feeder = settings.Source != ScanSource.Flatbed;
            // 1 = alimentador, 2 = mesa, 4 = frente e verso.
            TrySet(device.Properties, PropDocumentHandlingSelect,
                settings.Source switch { ScanSource.Feeder => 1, ScanSource.FeederDuplex => 5, _ => 2 });
            TrySet(device.Properties, PropPages, 1);

            dynamic item = device.Items[1];
            TrySet(item.Properties, PropCurrentIntent,
                settings.Color switch { ScanColor.Grayscale => 2, ScanColor.BlackAndWhite => 4, _ => 1 });
            TrySet(item.Properties, PropHorizontalResolution, settings.Dpi);
            TrySet(item.Properties, PropVerticalResolution, settings.Dpi);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                dynamic image;
                try
                {
                    image = item.Transfer(FormatBmp);
                }
                catch (COMException ex) when (ex.HResult == ErrorPaperEmpty && feeder && files.Count > 0)
                {
                    break; // o alimentador ficou vazio: fim normal
                }

                var path = Path.Combine(outputFolder, $"pagina-{stamp}-{files.Count + 1:0000}.bmp");
                if (File.Exists(path)) File.Delete(path);
                image.SaveFile(path);
                files.Add(path);
                pagesScanned?.Report(files.Count);
                if (!feeder) break;
            }

            return files;
        }
        catch (COMException ex)
        {
            throw new ScannerException(Describe(ex.HResult), ex);
        }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException ex)
        {
            throw new ScannerException("O scanner respondeu de forma inesperada: " + ex.Message, ex);
        }
    });

    private static object CreateManager()
    {
        var type = Type.GetTypeFromProgID("WIA.DeviceManager");
        if (type is null)
            throw new ScannerException("O serviço de digitalização do Windows (WIA) não está disponível neste computador.");
        try
        {
            return Activator.CreateInstance(type)
                   ?? throw new ScannerException("O serviço de digitalização do Windows (WIA) não respondeu.");
        }
        catch (COMException ex)
        {
            throw new ScannerException("O serviço de digitalização do Windows (WIA) não pôde ser iniciado. Verifique se o serviço \"Aquisição de Imagens do Windows\" está ativo.", ex);
        }
    }

    /// <summary>Define uma propriedade se o aparelho a tiver; propriedades não suportadas são ignoradas.</summary>
    private static void TrySet(dynamic properties, int id, int value)
    {
        try
        {
            foreach (dynamic property in properties)
            {
                if ((int)property.PropertyID != id) continue;
                property.Value = value;
                return;
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // O aparelho recusou o valor (ex.: resolução não suportada): fica o padrão dele.
        }
    }

    private static string Describe(int hresult) => unchecked((uint)hresult) switch
    {
        0x80210001 => "Erro geral do scanner. Desligue e ligue o aparelho e tente de novo.",
        0x80210002 => "O papel ficou preso no alimentador.",
        0x80210003 => "Não há papel no alimentador.",
        0x80210005 => "O scanner está desligado ou desconectado.",
        0x80210006 => "O scanner está ocupado. Aguarde a operação em andamento terminar.",
        0x80210007 => "O scanner está aquecendo. Tente de novo em instantes.",
        0x80210009 => "O scanner está travado ou a tampa está aberta.",
        0x8021000A => "Falha de comunicação com o scanner. Verifique o cabo ou a rede.",
        0x80210015 => "Nenhum scanner foi encontrado.",
        0x80210016 => "A tampa do scanner está aberta.",
        0x80210064 => "A digitalização foi cancelada no aparelho.",
        _ => $"O scanner informou um erro (código 0x{unchecked((uint)hresult):X8}).",
    };

    /// <summary>A automação do WIA é executada em uma thread STA própria, fora da interface.</summary>
    private static Task<T> RunSta<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (OperationCanceledException ex)
            {
                completion.SetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "PDFMaster.WIA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
