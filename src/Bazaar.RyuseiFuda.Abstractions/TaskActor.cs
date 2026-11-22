namespace Bazaar.RyuseiFuda.Kanban;

/// <summary>Who is making a change. <see cref="TaskActor.Id"/> is the agent or principal id, or <see cref="Guid.Empty"/> for the system.</summary>
public enum TaskActorKind
{
    System,
    Owner,
    Agent,
}

/// <summary>The actor recorded on every task event and used by the transition rules.</summary>
public readonly record struct TaskActor(TaskActorKind Kind, Guid Id)
{
    public static readonly TaskActor SystemActor = new(TaskActorKind.System, Guid.Empty);

    public string ToText() => Kind switch
    {
        TaskActorKind.System => "system",
        TaskActorKind.Owner => $"owner:{Id:D}",
        _ => $"agent:{Id:D}",
    };
}
