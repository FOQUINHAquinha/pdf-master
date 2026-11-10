using PDFMaster.Domain;

namespace PDFMaster.Core;

public interface ISettingsStore
{
    Task<IReadOnlyDictionary<string, string>> LoadAllAsync(CancellationToken ct = default);
    Task SaveAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct = default);
}

public interface IHistoryRepository
{
    Task<long> AddAsync(HistoryRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryQuery query, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetOperationNamesAsync(CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

public interface IRecentFilesRepository
{
    Task TouchAsync(string path, DateTime openedUtc, CancellationToken ct = default);
    Task<IReadOnlyList<RecentFile>> GetAsync(int limit, CancellationToken ct = default);
    Task RemoveAsync(string path, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

public interface IFileInspector
{
    /// <summary>Lê os dados básicos do arquivo e valida o cabeçalho. Nunca executa o arquivo.</summary>
    Task<FileEntry> InspectAsync(string path, bool computeHash, CancellationToken ct = default);
}
