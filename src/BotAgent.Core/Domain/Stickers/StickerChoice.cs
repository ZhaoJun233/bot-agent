namespace BotAgent.Domain.Stickers;

/// <summary>
/// 提示词里给模型挑的表情包候选（**只传 id + 说明，不传图**）。
///
/// 为什么在 Domain：它**是端口签名的一部分**（<c>IModelClient.CompleteAsync</c> 的入参），
/// 端口不能引用服务层的类型 —— 所以随端口一起下沉。
/// </summary>
public readonly record struct StickerChoice(string Id, string Description);
