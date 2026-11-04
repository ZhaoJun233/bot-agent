using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;

namespace BotAgent.Adapters.Panel.Zba;

/// <summary>
/// 把 ZBA 认证后的调用者映射到管理面权限。调用者由 <c>BotAgent.Panel</c> 在验证 Bearer 后构造，
/// 不来自请求头；这里只再检查应用权限位，避免 read token 借道能力动作提权。
/// </summary>
internal sealed class ZbaCallerAuthorization : IManagedAuthorization
{
    public ValueTask<bool> IsAllowedAsync(ManagedAuthorizationRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var caller = request.Caller;
        if (caller.AuthorityId != "zba" || caller.ResourceId != "bot") return ValueTask.FromResult(false);
        var read = (caller.Access & ConfigurationAccess.ReadOverview) != 0;
        var admin = (caller.Access & ConfigurationAccess.SaveConfiguration) != 0;
        var allowed = request.Permission switch
        {
            ManagedPermission.Inspect or ManagedPermission.Query => read,
            ManagedPermission.Execute or ManagedPermission.Cancel => admin,
            _ => false
        };
        return ValueTask.FromResult(allowed);
    }
}
