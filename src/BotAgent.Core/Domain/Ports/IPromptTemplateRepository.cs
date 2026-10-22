using System.Collections.Generic;
using System.Threading.Tasks;
using BotAgent.Domain.Prompts;

namespace BotAgent.Domain.Ports;

/// <summary>Prompt 模板与版本快照仓储端口（Domain 契约、零 IO）。</summary>
public interface IPromptTemplateRepository
{
    Task<IReadOnlyList<PromptTemplateSnapshot>> ListVersionsAsync(string key);

    Task<PromptTemplateSnapshot?> GetVersionAsync(string key, string versionId);

    Task<PromptTemplateSnapshot?> GetActiveVersionAsync(string key);

    Task<string> SaveVersionAsync(string key, string content, string? label = null);

    Task<bool> ActivateVersionAsync(string key, string versionId);

    string GetBuiltinDefault(string key);
}
