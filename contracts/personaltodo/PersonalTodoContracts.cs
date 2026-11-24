using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Todo;

public static class PersonalTodoStatus
{
    public const string Open = "open";
    public const string Done = "done";
    public const string Archived = "archived";

    public static bool IsValid(string? status) => status is Open or Done or Archived;
}

public static class PersonalTodoLimits
{
    public const int MaxPerOwner = 256;
    public const int MaxTitleChars = 200;
    public const int MaxNotesChars = 1000;
    public const int MaxSourceHintChars = 120;
}

public sealed record PersonalTodoItem(
    string TodoId,
    string OwnerScopeKey,
    string Title,
    string? Notes,
    int Priority,
    string Status,
    long? DueUnix,
    string? LinkedJobId,
    string? SourceHint,
    long CreatedUnix,
    long UpdatedUnix);

public sealed record PersonalTodoMutationResult(
    bool Ok,
    bool Replayed,
    string ReasonCode,
    PersonalTodoItem? Item = null,
    long Revision = 0);

public sealed record PersonalTodoSnapshotView(
    long Revision,
    IReadOnlyList<PersonalTodoItem> Items);

/// <summary>Pure storage port implemented by <c>BotAgent.Storage.PersonalTodoStore</c> (R9).</summary>
public interface IPersonalTodoStore
{
    PersonalTodoSnapshotView ReadOwner(string ownerScopeKey);

    PersonalTodoMutationResult SaveItem(
        string ownerScopeKey,
        PersonalTodoItem item,
        long? expectedRevision = null,
        string? idempotencyKey = null,
        string? idempotencyDigest = null);

    PersonalTodoMutationResult DeleteItem(
        string ownerScopeKey,
        string todoId,
        long? expectedRevision = null,
        string? idempotencyKey = null);
}

/// <summary>Narrow host port exposed to <c>preset.feature.personal-todo</c>.</summary>
public interface IPersonalTodoHostPorts
{
    PersonalTodoMutationResult Add(string sourceKey, string title, string? notes, int priority, string? dueText, string? sourceHint, string? timezoneIana);

    PersonalTodoItem[] List(string sourceKey, bool includeDone = false);

    PersonalTodoMutationResult Complete(string sourceKey, string todoId);

    PersonalTodoMutationResult Delete(string sourceKey, string todoId);
}

/// <summary>Package contribution exposed by <c>preset.feature.personal-todo</c>.</summary>
public interface IPersonalTodoContribution : IPluginContribution
{
    bool IsReady { get; }

    PersonalTodoMutationResult Add(string sourceKey, string title, string? notes, int priority, string? dueText, string? sourceHint, string? timezoneIana);

    PersonalTodoItem[] List(string sourceKey, bool includeDone = false);

    PersonalTodoMutationResult Complete(string sourceKey, string todoId);

    PersonalTodoMutationResult Delete(string sourceKey, string todoId);
}
