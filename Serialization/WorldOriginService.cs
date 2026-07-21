using System.Numerics;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.ECS;

namespace ArisenEngine.Resources.Serialization;

public readonly record struct WorldOriginRebase(
    long Sequence,
    WorldPosition PreviousOrigin,
    WorldPosition CurrentOrigin,
    WorldPosition LocalTranslation,
    int ShiftedTransformCount);

public readonly record struct WorldOriginSnapshot(
    WorldPosition Origin,
    WorldPosition PrimarySource,
    bool HasPrimarySource,
    long RebaseSequence,
    WorldPosition GridSize,
    WorldPosition Hysteresis);

public interface IWorldOriginService
{
    WorldPosition CurrentOrigin { get; }
    long RebaseSequence { get; }

    event Action<WorldOriginRebase>? RebaseStarting;
    event Action<WorldOriginRebase>? Rebased;

    WorldOriginSnapshot GetSnapshot();
    WorldPosition ToWorld(Vector3 originRelativePosition);
    bool TryToOriginRelative(WorldPosition worldPosition, out Vector3 originRelativePosition);
}

public sealed class WorldOriginService : IWorldOriginService
{
    private readonly object m_Gate = new();
    private WorldPosition m_CurrentOrigin;
    private WorldPosition m_PrimarySource;
    private WorldPosition m_GridSize = new(1024.0, 1024.0, 1024.0);
    private WorldPosition m_Hysteresis = new(1536.0, 1536.0, 1536.0);
    private WorldPosition m_PartitionOrigin;
    private bool m_HasPrimarySource;
    private long m_RebaseSequence;

    public WorldPosition CurrentOrigin
    {
        get
        {
            lock (m_Gate) return m_CurrentOrigin;
        }
    }

    public long RebaseSequence
    {
        get
        {
            lock (m_Gate) return m_RebaseSequence;
        }
    }

    public event Action<WorldOriginRebase>? RebaseStarting;
    public event Action<WorldOriginRebase>? Rebased;

    public WorldOriginSnapshot GetSnapshot()
    {
        lock (m_Gate)
        {
            return new WorldOriginSnapshot(
                m_CurrentOrigin,
                m_PrimarySource,
                m_HasPrimarySource,
                m_RebaseSequence,
                m_GridSize,
                m_Hysteresis);
        }
    }

    public WorldPosition ToWorld(Vector3 originRelativePosition)
    {
        if (!IsFinite(originRelativePosition))
        {
            throw new ArgumentException("Origin-relative position must be finite.", nameof(originRelativePosition));
        }

        WorldPosition origin = CurrentOrigin;
        return new WorldPosition(
            origin.X + originRelativePosition.X,
            origin.Y + originRelativePosition.Y,
            origin.Z + originRelativePosition.Z);
    }

    public bool TryToOriginRelative(
        WorldPosition worldPosition,
        out Vector3 originRelativePosition)
    {
        if (!worldPosition.IsFinite)
        {
            originRelativePosition = default;
            return false;
        }

        WorldPosition origin = CurrentOrigin;
        return TryToVector3(
            new WorldPosition(
                worldPosition.X - origin.X,
                worldPosition.Y - origin.Y,
                worldPosition.Z - origin.Z),
            out originRelativePosition);
    }

    internal void ConfigureForWorld(WorldPartitionSettings partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        double multiplier = Math.Max(1, partition.UnloadHysteresis) + 0.5;
        lock (m_Gate)
        {
            m_CurrentOrigin = default;
            m_PrimarySource = default;
            m_PartitionOrigin = partition.Origin;
            m_GridSize = partition.CellSize;
            m_Hysteresis = new WorldPosition(
                partition.CellSize.X * multiplier,
                partition.CellSize.Y * multiplier,
                partition.CellSize.Z * multiplier);
            m_HasPrimarySource = false;
            m_RebaseSequence = 0;
        }
    }

    internal void RequestPrimarySource(WorldPosition source)
    {
        if (!source.IsFinite)
        {
            throw new ArgumentException("World-origin source must be finite.", nameof(source));
        }

        lock (m_Gate)
        {
            m_PrimarySource = source;
            m_HasPrimarySource = true;
        }
    }

    internal void ClearPrimarySource()
    {
        lock (m_Gate) m_HasPrimarySource = false;
    }

    internal bool ProcessAtFrameBoundary(EntityManager entityManager)
    {
        ArgumentNullException.ThrowIfNull(entityManager);
        using var _ = Profiler.Zone("WorldOrigin.FrameBoundaryRebase");

        WorldPosition previous;
        WorldPosition selected;
        long sequence;
        lock (m_Gate)
        {
            if (!m_HasPrimarySource)
            {
                return false;
            }

            previous = m_CurrentOrigin;
            selected = SelectOriginLocked(previous, m_PrimarySource);
            if (selected == previous)
            {
                return false;
            }
            sequence = m_RebaseSequence + 1;
        }

        WorldPosition translation = new(
            previous.X - selected.X,
            previous.Y - selected.Y,
            previous.Z - selected.Z);
        if (!TryToVector3(translation, out Vector3 translationFloat))
        {
            throw new InvalidOperationException(
                $"World-origin rebase translation ({translation.X}, {translation.Y}, {translation.Z}) " +
                "cannot be represented by origin-relative ECS transforms.");
        }

        ComponentPool<TransformComponent> transformPool = entityManager.GetPool<TransformComponent>();
        TransformComponent[] transforms = transformPool.GetRawComponentArray();
        int transformCount = transformPool.Count;
        for (int index = 0; index < transformCount; index++)
        {
            Vector3 shifted = transforms[index].Position + translationFloat;
            if (!IsFinite(shifted))
            {
                throw new InvalidOperationException(
                    $"World-origin rebase would produce a non-finite transform at dense index {index}.");
            }
        }

        var rebase = new WorldOriginRebase(
            sequence,
            previous,
            selected,
            translation,
            transformCount);
        RebaseStarting?.Invoke(rebase);
        for (int index = 0; index < transformCount; index++)
        {
            transforms[index].Position += translationFloat;
        }

        lock (m_Gate)
        {
            m_CurrentOrigin = selected;
            m_RebaseSequence = sequence;
        }

        Profiler.PlotValue("WorldOrigin.RebaseSequence", sequence);
        Profiler.PlotValue("WorldOrigin.ShiftedTransforms", transformCount);
        Profiler.PlotValue("WorldOrigin.X", selected.X);
        Profiler.PlotValue("WorldOrigin.Y", selected.Y);
        Profiler.PlotValue("WorldOrigin.Z", selected.Z);
        Rebased?.Invoke(rebase);
        return true;
    }

    private WorldPosition SelectOriginLocked(WorldPosition current, WorldPosition source)
    {
        return new WorldPosition(
            SelectAxis(current.X, source.X, m_PartitionOrigin.X, m_GridSize.X, m_Hysteresis.X),
            SelectAxis(current.Y, source.Y, m_PartitionOrigin.Y, m_GridSize.Y, m_Hysteresis.Y),
            SelectAxis(current.Z, source.Z, m_PartitionOrigin.Z, m_GridSize.Z, m_Hysteresis.Z));
    }

    private static double SelectAxis(
        double current,
        double source,
        double partitionOrigin,
        double gridSize,
        double hysteresis)
    {
        if (Math.Abs(source - current) <= hysteresis)
        {
            return current;
        }

        return partitionOrigin + Math.Floor((source - partitionOrigin) / gridSize) * gridSize;
    }

    private static bool TryToVector3(WorldPosition value, out Vector3 result)
    {
        if (!value.IsFinite ||
            Math.Abs(value.X) > float.MaxValue ||
            Math.Abs(value.Y) > float.MaxValue ||
            Math.Abs(value.Z) > float.MaxValue)
        {
            result = default;
            return false;
        }

        result = new Vector3((float)value.X, (float)value.Y, (float)value.Z);
        return IsFinite(result);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

internal static class SceneStagingPlacement
{
    public static SceneStagingData PlaceCell(
        SceneStagingData staging,
        WorldPosition cellOrigin,
        WorldPosition renderOrigin)
    {
        ArgumentNullException.ThrowIfNull(staging);
        WorldPosition translation = new(
            cellOrigin.X - renderOrigin.X,
            cellOrigin.Y - renderOrigin.Y,
            cellOrigin.Z - renderOrigin.Z);
        if (!translation.IsFinite ||
            Math.Abs(translation.X) > float.MaxValue ||
            Math.Abs(translation.Y) > float.MaxValue ||
            Math.Abs(translation.Z) > float.MaxValue)
        {
            throw new InvalidOperationException(
                $"Cell-to-origin translation ({translation.X}, {translation.Y}, {translation.Z}) is not representable.");
        }

        var offset = new Vector3((float)translation.X, (float)translation.Y, (float)translation.Z);
        var placed = new SceneStagingEntity[staging.Entities.Length];
        for (int index = 0; index < staging.Entities.Length; index++)
        {
            SceneStagingEntity entity = staging.Entities[index];
            TransformComponent transform = entity.Transform;
            transform.Position += offset;
            if (!float.IsFinite(transform.Position.X) ||
                !float.IsFinite(transform.Position.Y) ||
                !float.IsFinite(transform.Position.Z))
            {
                throw new InvalidOperationException(
                    $"Cell placement produced a non-finite transform for entity '{entity.AuthoringGuid:D}'.");
            }
            placed[index] = entity with { Transform = transform };
        }

        return staging with { Entities = placed };
    }
}
