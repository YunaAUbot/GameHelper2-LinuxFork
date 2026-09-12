using System;
using System.Numerics;

namespace LootValue;

internal readonly record struct ScrollRect(Vector2 Position, Vector2 Size);
internal readonly record struct ScrollGeometry(float ContentHeight, float OffsetY, float ClipTop, float ClipBottom);

/// <summary>Reject unrelated UI layouts before probing their potentially hidden content nodes.</summary>
internal static class ScrollContainerGeometry
{
    internal static ScrollProbeStatus Probe(Func<nint, ScrollRect?> read, nint content, nint track, nint thumb,
        out ScrollGeometry geometry)
    {
        geometry = default;
        var trackRect = read(track);
        if (trackRect is not { } bar) return ScrollProbeStatus.Unavailable;
        if (bar.Size.X < 4f || bar.Size.X > 64f || bar.Size.Y < 40f)
            return ScrollProbeStatus.NotApplicable;

        var thumbRect = read(thumb);
        if (thumbRect is not { } knob) return ScrollProbeStatus.Unavailable;
        if (knob.Size.X < 2f || knob.Size.X > bar.Size.X * 1.5f ||
            knob.Size.Y < 8f || knob.Size.Y >= bar.Size.Y ||
            knob.Position.Y < bar.Position.Y - 2f ||
            knob.Position.Y + knob.Size.Y > bar.Position.Y + bar.Size.Y + 2f)
            return ScrollProbeStatus.NotApplicable;

        var contentRect = read(content);
        if (contentRect is not { } items) return ScrollProbeStatus.Unavailable;
        if (items.Size.Y <= bar.Size.Y + 1f || bar.Position.X < items.Position.X)
            return ScrollProbeStatus.NotApplicable;

        var fraction = Math.Clamp((knob.Position.Y - bar.Position.Y) / (bar.Size.Y - knob.Size.Y), 0f, 1f);
        var offset = fraction * (items.Size.Y - bar.Size.Y);
        if (!float.IsFinite(offset) || !(bar.Position.Y + bar.Size.Y > bar.Position.Y))
            return ScrollProbeStatus.NotApplicable;
        geometry = new(items.Size.Y, offset, bar.Position.Y, bar.Position.Y + bar.Size.Y);
        return ScrollProbeStatus.Succeeded;
    }
}
