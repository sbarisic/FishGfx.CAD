using System.Numerics;
using FishGfx.Graphics;

namespace FishGfx.CFD;

internal readonly record struct CfdStreamlinePoint(Vector3 Position, Vector3 Velocity, double Speed);
internal sealed record CfdStreamline(CfdStreamlinePoint[] Points);
internal sealed record CfdStreamlineResult(int FrameIndex, string VelocityChecksum, CfdStreamline[] Lines);

internal sealed class CfdSpatialSampleIndex
{
	private const int NeighborRadius = 2;
	private const int NeighborCount = 8;
	private readonly VtkVector[] points;
	private readonly float cellSize;
	private readonly Dictionary<(int X, int Y, int Z), List<int>> cells = [];

	internal CfdSpatialSampleIndex(VtkVector[] points, float? requestedCellSize = null)
	{
		this.points = points;
		Vector3 minimum = new((float)points.Min(v => v.X), (float)points.Min(v => v.Y), (float)points.Min(v => v.Z));
		Vector3 maximum = new((float)points.Max(v => v.X), (float)points.Max(v => v.Y), (float)points.Max(v => v.Z));
		Vector3 extent = maximum - minimum;
		float[] positiveExtents = [extent.X, extent.Y, extent.Z];
		cellSize = requestedCellSize ?? positiveExtents.Where(value => value > 0).DefaultIfEmpty(1).Min() / 35;
		if (!(cellSize > 0)) throw new InvalidDataException("The CFD sample positions have no spatial extent.");
		for (int index = 0; index < points.Length; ++index)
		{
			var cell = Cell(Point(points[index]));
			if (!cells.TryGetValue(cell, out List<int>? members)) cells.Add(cell, members = []);
			members.Add(index);
		}
	}

	internal VtkVector[] Points => points;
	internal float CellSize => cellSize;

	internal bool TryNeighbors(Vector3 position, out (float Distance, int Index)[] nearest)
	{
		var center = Cell(position);
		List<(float Distance, int Index)> candidates = [];
		for (int x = -NeighborRadius; x <= NeighborRadius; ++x)
		for (int y = -NeighborRadius; y <= NeighborRadius; ++y)
		for (int z = -NeighborRadius; z <= NeighborRadius; ++z)
		{
			if (!cells.TryGetValue((center.X + x, center.Y + y, center.Z + z), out List<int>? members)) continue;
			foreach (int index in members)
				candidates.Add((Vector3.DistanceSquared(position, Point(points[index])), index));
		}
		candidates.Sort((left, right) => left.Distance.CompareTo(right.Distance));
		if (candidates.Count == 0 || candidates[0].Distance > cellSize * cellSize * 6.25f)
		{
			nearest = [];
			return false;
		}
		nearest = candidates.Take(NeighborCount).ToArray();
		return true;
	}

	private (int X, int Y, int Z) Cell(Vector3 point) => (
		(int)MathF.Floor(point.X / cellSize),
		(int)MathF.Floor(point.Y / cellSize),
		(int)MathF.Floor(point.Z / cellSize));

	internal static Vector3 Point(VtkVector value) => new((float)value.X, (float)value.Y, (float)value.Z);
}

internal sealed class CfdVelocityFrameSampler
{
	private readonly CfdSpatialSampleIndex index;
	private readonly VtkVector[] velocities;

	internal CfdVelocityFrameSampler(CfdSpatialSampleIndex index, VtkVector[] velocities)
	{
		if (index.Points.Length != velocities.Length)
			throw new ArgumentException("Velocity and point arrays must have equal lengths.");
		this.index = index;
		this.velocities = velocities;
	}

	internal bool TrySample(Vector3 position, out Vector3 velocity)
	{
		if (!index.TryNeighbors(position, out var nearest))
		{
			velocity = default;
			return false;
		}
		Vector3 weighted = Vector3.Zero;
		double totalWeight = 0;
		foreach ((float distance, int point) in nearest)
		{
			double weight = 1 / Math.Max(distance, index.CellSize * index.CellSize * 1e-5f);
			weighted += CfdSpatialSampleIndex.Point(velocities[point]) * (float)weight;
			totalWeight += weight;
		}
		velocity = weighted / (float)totalWeight;
		return float.IsFinite(velocity.X) && float.IsFinite(velocity.Y) && float.IsFinite(velocity.Z);
	}
}

// Compatibility adapter for existing sampling tests and callers. New frame-aware
// code owns the immutable spatial index separately.
internal sealed class CfdVelocityFieldSampler
{
	private readonly CfdVelocityFrameSampler sampler;

	internal CfdVelocityFieldSampler(VtkVector[] points, VtkVector[] velocities, float cellSize)
	{
		sampler = new(new CfdSpatialSampleIndex(points, cellSize), velocities);
	}

	internal bool TrySample(Vector3 position, out Vector3 velocity) => sampler.TrySample(position, out velocity);
}

internal static class CfdStreamlineTracer
{
	internal static CfdStreamline[] Trace(
		CfdSpatialSampleIndex index,
		VtkVector[] velocities,
		IReadOnlyList<Vector3> seeds,
		CancellationToken cancellationToken)
	{
		Vector3 minimum = new((float)index.Points.Min(v => v.X), (float)index.Points.Min(v => v.Y), (float)index.Points.Min(v => v.Z));
		Vector3 maximum = new((float)index.Points.Max(v => v.X), (float)index.Points.Max(v => v.Y), (float)index.Points.Max(v => v.Z));
		Vector3 extent = maximum - minimum;
		float stepLength = new[] { extent.X, extent.Y, extent.Z }.Where(value => value > 0).Min() / 90;
		double maximumSpeed = velocities.Max(value => value.Length);
		if (!(stepLength > 0) || !(maximumSpeed > 0)) return [];
		CfdVelocityFrameSampler sampler = new(index, velocities);
		List<CfdStreamline> result = [];
		foreach (Vector3 seed in seeds)
		{
			cancellationToken.ThrowIfCancellationRequested();
			CfdStreamline? line = TraceOne(sampler, seed, stepLength, extent.Length() * 2.5f, maximumSpeed, cancellationToken);
			if (line != null) result.Add(line);
		}
		return result.ToArray();
	}

	private static CfdStreamline? TraceOne(
		CfdVelocityFrameSampler sampler,
		Vector3 seed,
		float stepLength,
		float maximumLength,
		double maximumSpeed,
		CancellationToken cancellationToken)
	{
		if (!sampler.TrySample(seed, out Vector3 initialVelocity) || initialVelocity.LengthSquared() <= 1e-12f) return null;
		Vector3 position = seed + Vector3.Normalize(initialVelocity) * stepLength * 0.75f;
		List<CfdStreamlinePoint> points = [];
		float tracedLength = 0;
		for (int step = 0; step < 360 && tracedLength < maximumLength; ++step)
		{
			if ((step & 15) == 0) cancellationToken.ThrowIfCancellationRequested();
			if (!sampler.TrySample(position, out Vector3 firstVelocity)) break;
			double speed = firstVelocity.Length();
			if (speed <= maximumSpeed * 0.001) break;
			points.Add(new(position, firstVelocity, speed));
			Vector3 midpoint = position + Vector3.Normalize(firstVelocity) * (stepLength * 0.5f);
			if (!sampler.TrySample(midpoint, out Vector3 midpointVelocity) || midpointVelocity.LengthSquared() <= 1e-12f) break;
			Vector3 next = position + Vector3.Normalize(midpointVelocity) * stepLength;
			if (points.Count > 32 && points.Take(points.Count - 24).Any(value =>
				Vector3.DistanceSquared(value.Position, next) < stepLength * stepLength * 0.3f)) break;
			position = next;
			tracedLength += stepLength;
		}
		return points.Count >= 4 ? new(points.ToArray()) : null;
	}
}

internal sealed class CfdStreamlineCache
{
	private const int Capacity = 8;
	private readonly Dictionary<(int Frame, string Checksum), CfdStreamline[]> values = [];
	private readonly LinkedList<(int Frame, string Checksum)> order = [];

	internal bool TryGet(int frame, string checksum, out CfdStreamline[] lines)
	{
		var key = (frame, checksum);
		if (!values.TryGetValue(key, out lines!)) return false;
		order.Remove(key);
		order.AddLast(key);
		return true;
	}

	internal void Add(CfdStreamlineResult result)
	{
		var key = (result.FrameIndex, result.VelocityChecksum);
		values[key] = result.Lines;
		order.Remove(key);
		order.AddLast(key);
		while (order.Count > Capacity)
		{
			var oldest = order.First!.Value;
			order.RemoveFirst();
			values.Remove(oldest);
		}
	}
}

internal sealed partial class CfdViewerApplication
{
	private static IReadOnlyList<Vector3> SelectInletSeeds(LegacyVtkDataSet inlet, int count)
	{
		if (inlet.Points.Length == 0) return [];
		Vector3[] points = inlet.Points.Select(Point).ToArray();
		Vector3 center = points.Aggregate(Vector3.Zero, (sum, point) => sum + point) / points.Length;
		List<Vector3> selectedBoundaryPoints = [];
		List<Vector3> seeds = [center];
		while (seeds.Count < count)
		{
			Vector3 candidate = points.OrderByDescending(point => selectedBoundaryPoints.Count == 0
				? Vector3.DistanceSquared(point, center)
				: selectedBoundaryPoints.Min(selected => Vector3.DistanceSquared(point, selected))).First();
			selectedBoundaryPoints.Add(candidate);
			seeds.Add(Vector3.Lerp(center, candidate, 0.58f));
		}
		return seeds;
	}

	private void DrawVelocityArrows(RenderPass pass)
	{
		foreach (VelocityArrow arrow in arrows)
		{
			pass.DrawLine(new Vertex3(arrow.Start, arrow.Color), new Vertex3(arrow.End, arrow.Color), 2.3f);
			DrawArrowHead(pass, arrow.Start, arrow.End, arrow.Color, Vector3.Distance(arrow.Start, arrow.End) * 0.34f);
		}
	}

	private void DrawStreamlines(RenderPass pass)
	{
		double maximumSpeed = velocityMaximum > 0 ? velocityMaximum : 1;
		foreach (CfdStreamline line in streamlines)
		for (int index = 1; index < line.Points.Length; ++index)
		{
			CfdStreamlinePoint previous = line.Points[index - 1];
			CfdStreamlinePoint current = line.Points[index];
			Color color = FieldColor(current.Speed / maximumSpeed);
			pass.DrawLine(new Vertex3(previous.Position, color), new Vertex3(current.Position, color), 2.2f);
			if (index % 28 == 0) DrawArrowHead(pass, previous.Position, current.Position, color, orbitDistance * 0.008f);
		}
	}

	private void DrawArrowHead(RenderPass pass, Vector3 start, Vector3 end, Color color, float headLength)
	{
		Vector3 direction = end - start;
		if (direction.LengthSquared() <= 1e-12f) return;
		direction = Vector3.Normalize(direction);
		Vector3 side = Vector3.Cross(direction, camera.WorldForwardNormal);
		if (side.LengthSquared() <= 1e-8f) side = Vector3.Cross(direction, camera.WorldUpNormal);
		if (side.LengthSquared() <= 1e-8f) return;
		side = Vector3.Normalize(side);
		Vector3 back = end - direction * headLength;
		pass.DrawLine(new Vertex3(end, color), new Vertex3(back + side * headLength * 0.5f, color), 2.3f);
		pass.DrawLine(new Vertex3(end, color), new Vertex3(back - side * headLength * 0.5f, color), 2.3f);
	}
}
