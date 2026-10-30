using BotAgent.Services.Qq;

namespace BotAgent.Services.Agent;

public sealed partial class AgentCommandService
{
    private async Task RunAdmittedServerAsync(AgentTask task, ServerAgentAdmission admission)
    {
        try
        {
            using var sourceScope = PlatformSourceExecution.Enter(task.SourceLease);
            await _serverAgent.RunAsync(task, admission.Token);
        }
        catch (OperationCanceledException)
        {
            task.Fail("已取消");
        }
        catch (Exception ex)
        {
            task.Fail("任务执行失败（" + ex.GetType().Name + "）");
        }
        finally
        {
            admission.Dispose();
            OnAgentFinished(task);
        }
    }
}
