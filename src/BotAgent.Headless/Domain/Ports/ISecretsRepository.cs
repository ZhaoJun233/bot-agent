namespace BotAgent.Domain.Ports;

/// <summary>
/// 面板里填的**密钥**（模型 API Key、网易云登录态、云端 TTS 的厂商密钥、官方通道 AppSecret）的存取端口
/// （由 <c>Adapters/Persistence/SecretsStore</c> 实现，见 §6.4）。
///
/// 规矩不变（与实现一一对应）：**面板只回显掩码**、不写进 settings、环境变量优先于"从没在面板里填过"。
/// 为什么要有它：用例（语音合成取 TTS 密钥）只该说"把密钥给我"，不该知道它存在哪张表、什么时候 chmod。
/// </summary>
public interface ISecretsRepository
{
    /// <summary>模型 API Key（没有 = null，调用方回退环境变量）。</summary>
    string? LoadApiKey();

    /// <summary>写模型 API Key（空值 = 删掉该条）。</summary>
    bool SaveApiKey(string? apiKey);

    /// <summary>服务器 agent 专用密钥。</summary>
    string? LoadAgentServerKey();

    bool SaveAgentServerKey(string? apiKey);

    /// <summary>网易云登录态（扫码成功后存下来）。</summary>
    string? LoadNeteaseCookie();

    bool SaveNeteaseCookie(string? cookie);

    /// <summary>云端 TTS 的厂商密钥。</summary>
    string? LoadTtsKey();

    bool SaveTtsKey(string? key);

    /// <summary>官方通道（QQ 开放平台）的 AppSecret。</summary>
    string? LoadOfficialSecret();

    bool SaveOfficialSecret(string? secret);

    /// <summary>读一条密钥（没有 / 读失败 = null）。</summary>
    string? Load(string name);

    /// <summary>写一条密钥（空值 = 删掉该条）。</summary>
    bool Save(string name, string? value);
}
