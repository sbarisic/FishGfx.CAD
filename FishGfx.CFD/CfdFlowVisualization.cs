using System.Numerics;
using FishGfx.Graphics;

namespace FishGfx.CFD;

internal sealed partial class CfdViewerApplication
{
	private void BuildStreamlines(IReadOnlyList<CfdBoundaryPatch> inletPatches)
	{
		if (volume == null
			|| inletPatches.Count == 0
			|| !volume.PointVectors.TryGetValue("U", out VtkVector[]? velocities))
		{
			return;
		}
		Vector3 minimum = new(
			(float)volume.Points.Min(item => item.X),
			(float)volume.Points.Min(item => item.Y),
			(float)volume.Points.Min(item => item.Z));
		Vector3 maximum = new(
			(float)volume.Points.Max(item => item.X),
			(float)volume.Points.Max(item => item.Y),
			(float)volume.Points.Max(item => item.Z));
		Vector3 extent = maximum - minimum;
		float minimumExtent = new[] { extent.X, extent.Y, extent.Z }.Where(value => value > 0).Min();
		float cellSize = minimumExtent / 35;
		float stepLength = minimumExtent / 90;
		double maximumSpeed = velocities.Max(item => item.Length);
		if (!(cellSize > 0) || !(stepLength > 0) || !(maximumSpeed > 0)) return;

		CfdVelocityFieldSampler sampler = new(volume.Points, velocities, cellSize);
		foreach (CfdBoundaryPatch inlet in inletPatches.OrderBy(item => item.Name, StringComparer.Ordinal))
		{
			foreach (Vector3 seed in SelectInletSeeds(inlet.Data, 5))
			{
				CfdStreamline? line = TraceStreamline(
					sampler,
					seed,
					stepLength,
					extent.Length() * 2.5f,
					maximumSpeed);
				if (line != null) streamlines.Add(line);
			}
		}
	}

	private static IReadOnlyList<Vector3> SelectInletSeeds(LegacyVtkDataSet inlet, int count)
	{
		if (inlet.Points.Length == 0) return [];
		Vector3[] points = inlet.Points.Select(Point).ToArray();
		Vector3 center = points.Aggregate(Vector3.Zero, (sum, point) => sum + point) / points.Length;
		List<Vector3> selectedBoundaryPoints = [];
		List<Vector3> seeds = [center];
		while (seeds.Count < count)
		{
			Vector3 candidate = points
				.OrderByDescending(point => selectedBoundaryPoints.Count == 0
					? Vector3.DistanceSquared(point, center)
					: selectedBoundaryPoints.Min(selected => Vector3.DistanceSquared(point, selected)))
				.First();
			selectedBoundaryPoints.Add(candidate);
			seeds.Add(Vector3.Lerp(center, candidate, 0.58f));
		}
		return seeds;
	}

	private static CfdStreamline? TraceStreamline(
		CfdVelocityFieldSampler sampler,
		Vector3 seed,
		float stepLength,
		float maximumLength,
		double maximumSpeed)
	{
		if (!sampler.TrySample(seed, out Vector3 initialVelocity)) return null;
		if (initialVelocity.LengthSquared() <= 1e-12f) return null;
		Vector3 position = seed + Vector3.Normalize(initialVelocity) * stepLength * 0.75f;
		List<CfdStreamlinePoint> points = [];
		float tracedLength = 0;
		for (int step = 0; step < 360 && tracedLength < maximumLength; ++step)
		{
			if (!sampler.TrySample(position, out Vector3 firstVelocity)) break;
			double speed = firstVelocity.Length();
			if (speed <= maximumSpeed * 0.001) break;
			points.Add(new CfdStreamlinePoint(position, firstVelocity, speed));
			Vector3 midpoint = position + Vector3.Normalize(firstVelocity) * (stepLength * 0.5f);
			if (!sampler.TrySample(midpoint, out Vector3 midpointVelocity)
				|| midpointVelocity.LengthSquared() <= 1e-12f)
			{
				break;
			}
			Vector3 next = position + Vector3.Normalize(midpointVelocity) * stepLength;
			if (points.Count > 32
				&& points.Take(points.Count - 24).Any(item =>
					Vector3.DistanceSquared(item.Position, next) < stepLength * stepLength * 0.3f))
			{
				break;
			}
			position = next;
			tracedLength += stepLength;
		}
		return points.Count >= 4 ? new CfdStreamline(points.ToArray()) : null;
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
		{
			for (int index = 1; index < line.Points.Length; ++index)
			{
				CfdStreamlinePoint previous = line.Points[index - 1];
				CfdStreamlinePoint current = line.Points[index];
				Color color = FieldColor(current.Speed / maximumSpeed);
				pass.DrawLine(new Vertex3(previous.Position, color), new Vertex3(current.Position, color), 2.2f);
				if (index % 28 == 0)
					DrawArrowHead(pass, previous.Position, current.Position, color, orbitDistance * 0.008f);
			}
		}
	}

	private void DrawArrowHead(
		RenderPass pass,
		Vector3 start,
		Vector3 end,
		Color color,
		float headLength)
	{
		Vector3 direction = end - start;
		if (direction.LengthSquared() <= 1e-12f) return;
		direction = Vector3.Normalize(direction);
		Vector3 side = Vector3.Cross(direction, camera.WorldForwardNormal);
		if (side.LengthSquared() <= 1e-8f) side = Vector3.Cross(direction, camera.WorldUpNormal);
		if (side.LengthSquared() <= 1e-8f) return;
		side = Vector3.Normalize(side);
		Vector3 back = end - direction * headLength;
		Vector3 left = back + side * headLength * 0.5f;
		Vector3 right = back - side * headLength * 0.5f;
		pass.DrawLine(new Vertex3(end, color), new Vertex3(left, color), 2.3f);
		pass.DrawLine(new Vertex3(end, color), new Vertex3(right, color), 2.3f);
	}

	private sealed record CfdStreamline(CfdStreamlinePoint[] Points);
	private readonly record struct CfdStreamlinePoint(Vector3 Position, Vector3 Velocity, double Speed);
}

internal sealed class CfdVelocityFieldSampler
{
	private const int NeighborRadius = 2;
	private const int NeighborCount = 8;
	private readonly VtkVector[] points;
	private readonly VtkVector[] velocities;
	private readonly float cellSize;
	private readonly Dictionary<(int X, int Y, int Z), List<int>> cells = [];

	internal CfdVelocityFieldSampler(VtkVector[] points, VtkVector[] velocities, float cellSize)
	{
		if (points.Length != velocities.Length)
			throw new ArgumentException("Velocity and point arrays must have equal lengths.");
		if (!float.IsFinite(cellSize) || cellSize <= 0)
			throw new ArgumentOutOfRangeException(nameof(cellSize));
		this.points = points;
		this.velocities = velocities;
		this.cellSize = cellSize;
		for (int index = 0; index < points.Length; ++index)
		{
			var cell = Cell(Point(points[index]));
			if (!cells.TryGetValue(cell, out List<int>? members))
			{
				members = [];
				cells.Add(cell, members);
			}
			members.Add(index);
		}
	}

	internal bool TrySample(Vector3 position, out Vector3 velocity)
	{
		var center = Cell(position);
		List<(float Distance, int Index)> nearest = [];
		for (int x = -NeighborRadius; x <= NeighborRadius; ++x)
		for (int y = -NeighborRadius; y <= NeighborRadius; ++y)
		for (int z = -NeighborRadius; z <= NeighborRadius; ++z)
		{
			if (!cells.TryGetValue((center.X + x, center.Y + y, center.Z + z), out List<int>? members))
				continue;
			foreach (int index in members)
			{
				float distance = Vector3.DistanceSquared(position, Point(points[index]));
				nearest.Add((distance, index));
			}
		}
		if (nearest.Count == 0)
		{
			velocity = default;
			return false;
		}
		nearest.Sort((left, right) => left.Distance.CompareTo(right.Distance));
		if (nearest[0].Distance > cellSize * cellSize * 6.25f)
		{
			velocity = default;
			return false;
		}
		Vector3 weighted = Vector3.Zero;
		double totalWeight = 0;
		foreach ((float distance, int index) in nearest.Take(NeighborCount))
		{
			double weight = 1 / Math.Max(distance, cellSize * cellSize * 1e-5f);
			weighted += Point(velocities[index]) * (float)weight;
			totalWeight += weight;
		}
		velocity = weighted / (float)totalWeight;
		return float.IsFinite(velocity.X) && float.IsFinite(velocity.Y) && float.IsFinite(velocity.Z);
	}

	private (int X, int Y, int Z) Cell(Vector3 point) => (
		(int)MathF.Floor(point.X / cellSize),
		(int)MathF.Floor(point.Y / cellSize),
		(int)MathF.Floor(point.Z / cellSize));

	private static Vector3 Point(VtkVector value) => new((float)value.X, (float)value.Y, (float)value.Z);
}
