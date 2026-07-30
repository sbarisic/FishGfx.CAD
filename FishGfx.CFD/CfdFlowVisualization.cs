using System.Numerics;
using FishGfx.Graphics;

namespace FishGfx.CFD;

internal readonly record struct CfdStreamlinePoint(Vector3 Position, Vector3 Velocity, double Speed);
internal enum CfdStreamlineTermination
{
	Outlet,
	Wall,
	Inlet,
	SampleSupport,
	LowSpeed,
	Loop,
	TraceLimit,
}

internal sealed record CfdStreamline(CfdStreamlinePoint[] Points, CfdStreamlineTermination Termination);
internal sealed record CfdStreamlineResult(
	int FrameIndex,
	string VelocityChecksum,
	CfdStreamline[] Lines,
	bool IsCanceled = false);

internal sealed class CfdSpatialSampleIndex
{
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

	internal bool TryNeighbors(
		Vector3 position,
		int searchRadius,
		int maximumCount,
		out (float Distance, int Index)[] nearest)
	{
		if (searchRadius < 1) throw new ArgumentOutOfRangeException(nameof(searchRadius));
		if (maximumCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumCount));
		var center = Cell(position);
		List<(float Distance, int Index)> candidates = [];
		for (int x = -searchRadius; x <= searchRadius; ++x)
		for (int y = -searchRadius; y <= searchRadius; ++y)
		for (int z = -searchRadius; z <= searchRadius; ++z)
		{
			if (!cells.TryGetValue((center.X + x, center.Y + y, center.Z + z), out List<int>? members)) continue;
			foreach (int index in members)
				candidates.Add((Vector3.DistanceSquared(position, Point(points[index])), index));
		}
		candidates.Sort((left, right) => left.Distance.CompareTo(right.Distance));
		float maximumDistance = cellSize * (searchRadius + 0.5f);
		if (candidates.Count == 0 || candidates[0].Distance > maximumDistance * maximumDistance)
		{
			nearest = [];
			return false;
		}
		nearest = candidates.Take(maximumCount).ToArray();
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
	private const int InterpolationNeighborCount = 8;
	private const int InitialSearchRadius = 2;
	private const int ExpandedSearchRadius = 5;
	private const int FallbackSearchRadius = 9;
	private const int InitialCandidateCount = 32;
	private const int ExpandedCandidateCount = 96;
	private const int FallbackCandidateCount = 192;
	private readonly CfdSpatialSampleIndex index;
	private readonly VtkVector[] velocities;
	private readonly CfdBoundaryBvh? boundary;
	private readonly double minimumSampleSpeedSquared;

	internal CfdVelocityFrameSampler(
		CfdSpatialSampleIndex index,
		VtkVector[] velocities,
		CfdBoundaryBvh? boundary = null,
		double minimumSampleSpeed = 0)
	{
		if (index.Points.Length != velocities.Length)
			throw new ArgumentException("Velocity and point arrays must have equal lengths.");
		this.index = index;
		this.velocities = velocities;
		this.boundary = boundary;
		minimumSampleSpeedSquared = minimumSampleSpeed * minimumSampleSpeed;
	}

	internal bool TrySample(Vector3 position, out Vector3 velocity)
	{
		if (index.TryNeighbors(position, InitialSearchRadius, InitialCandidateCount, out var nearest)
			&& TryInterpolate(position, nearest, out velocity, out int visibleCount)
			&& visibleCount >= InterpolationNeighborCount)
		{
			return true;
		}
		if (index.TryNeighbors(position, ExpandedSearchRadius, ExpandedCandidateCount, out nearest)
			&& TryInterpolate(position, nearest, out velocity, out _))
		{
			return true;
		}
		if (!index.TryNeighbors(position, FallbackSearchRadius, FallbackCandidateCount, out nearest)
			|| !TryInterpolate(position, nearest, out velocity, out _))
		{
			return TryInterpolateAlongVisibleChain(position, out velocity);
		}
		return true;
	}

	private bool TryInterpolateAlongVisibleChain(Vector3 position, out Vector3 velocity)
	{
		if (boundary == null
			|| !index.TryNeighbors(position, FallbackSearchRadius, FallbackCandidateCount, out var nearest))
		{
			velocity = default;
			return false;
		}
		PriorityQueue<(int Point, int Depth, float PathLength), float> pending = new();
		HashSet<int> visited = [];
		HashSet<int> discovered = [];
		foreach ((float distanceSquared, int point) in nearest)
		{
			Vector3 sample = CfdSpatialSampleIndex.Point(index.Points[point]);
			if (!IsVisible(position, sample)) continue;
			float distance = MathF.Sqrt(distanceSquared);
			discovered.Add(point);
			pending.Enqueue((point, 0, distance), distance);
			if (pending.Count == 16) break;
		}
		Vector3 weighted = Vector3.Zero;
		double totalWeight = 0;
		int flowingSamples = 0;
		while (pending.TryDequeue(out var current, out _)
			&& visited.Count < 256
			&& flowingSamples < InterpolationNeighborCount)
		{
			if (!visited.Add(current.Point)) continue;
			Vector3 sampleVelocity = CfdSpatialSampleIndex.Point(velocities[current.Point]);
			if (sampleVelocity.LengthSquared() > minimumSampleSpeedSquared)
			{
				double weight = 1 / Math.Max(
					current.PathLength * current.PathLength,
					index.CellSize * index.CellSize * 1e-5f);
				weighted += sampleVelocity * (float)weight;
				totalWeight += weight;
				++flowingSamples;
				continue;
			}
			if (current.Depth >= 12) continue;
			Vector3 currentPosition = CfdSpatialSampleIndex.Point(index.Points[current.Point]);
			if (!index.TryNeighbors(currentPosition, InitialSearchRadius, InitialCandidateCount, out var adjacent))
				continue;
			foreach ((float distanceSquared, int point) in adjacent)
			{
				if (point == current.Point || discovered.Count >= 512 || !discovered.Add(point)) continue;
				Vector3 candidate = CfdSpatialSampleIndex.Point(index.Points[point]);
				if (!IsVisible(currentPosition, candidate)) continue;
				float pathLength = current.PathLength + MathF.Sqrt(distanceSquared);
				pending.Enqueue((point, current.Depth + 1, pathLength), pathLength);
			}
		}
		if (!(totalWeight > 0))
		{
			velocity = default;
			return false;
		}
		velocity = weighted / (float)totalWeight;
		return float.IsFinite(velocity.X) && float.IsFinite(velocity.Y) && float.IsFinite(velocity.Z);
	}

	private bool TryInterpolate(
		Vector3 position,
		IReadOnlyList<(float Distance, int Index)> nearest,
		out Vector3 velocity,
		out int visibleCount)
	{
		Vector3 weighted = Vector3.Zero;
		double totalWeight = 0;
		visibleCount = 0;
		foreach ((float distance, int point) in nearest)
		{
			Vector3 samplePosition = CfdSpatialSampleIndex.Point(index.Points[point]);
			if (!IsVisible(position, samplePosition)) continue;
			Vector3 sampleVelocity = CfdSpatialSampleIndex.Point(velocities[point]);
			if (sampleVelocity.LengthSquared() <= minimumSampleSpeedSquared) continue;
			double weight = 1 / Math.Max(distance, index.CellSize * index.CellSize * 1e-5f);
			weighted += sampleVelocity * (float)weight;
			totalWeight += weight;
			if (++visibleCount == InterpolationNeighborCount) break;
		}
		if (!(totalWeight > 0))
		{
			velocity = default;
			return false;
		}
		velocity = weighted / (float)totalWeight;
		return float.IsFinite(velocity.X) && float.IsFinite(velocity.Y) && float.IsFinite(velocity.Z);
	}

	private bool IsVisible(Vector3 position, Vector3 samplePosition)
	{
		if (boundary == null || !boundary.TryIntersectSegment(position, samplePosition, out CfdBoundaryHit hit))
			return true;
		float segmentLength = Vector3.Distance(position, samplePosition);
		return (1 - hit.SegmentParameter) * segmentLength <= boundary.Epsilon * 2;
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
	internal static CfdStreamlineResult Trace(
		CfdSpatialSampleIndex index,
		VtkVector[] velocities,
		IReadOnlyList<Vector3> seeds,
		CancellationToken cancellationToken,
		int frameIndex = 0,
		string velocityChecksum = "") => Trace(
			index,
			velocities,
			seeds.Select(value => new CfdStreamlineSeed(value, Vector3.Zero, string.Empty)).ToArray(),
			null,
			cancellationToken,
			frameIndex,
			velocityChecksum);

	internal static CfdStreamlineResult Trace(
		CfdSpatialSampleIndex index,
		VtkVector[] velocities,
		IReadOnlyList<CfdStreamlineSeed> seeds,
		CfdBoundaryBvh? boundary,
		CancellationToken cancellationToken,
		int frameIndex = 0,
		string velocityChecksum = "")
	{
		if (cancellationToken.IsCancellationRequested)
			return new(frameIndex, velocityChecksum, [], true);
		Vector3 minimum = new((float)index.Points.Min(v => v.X), (float)index.Points.Min(v => v.Y), (float)index.Points.Min(v => v.Z));
		Vector3 maximum = new((float)index.Points.Max(v => v.X), (float)index.Points.Max(v => v.Y), (float)index.Points.Max(v => v.Z));
		Vector3 extent = maximum - minimum;
		float stepLength = index.CellSize * 0.25f;
		double maximumSpeed = velocities.Max(value => value.Length);
		if (!(stepLength > 0) || !(maximumSpeed > 0))
			return new(frameIndex, velocityChecksum, []);
		CfdVelocityFrameSampler sampler = new(index, velocities, boundary, maximumSpeed * 1e-4);
		List<CfdStreamline> result = [];
		foreach (CfdStreamlineSeed seed in seeds)
		{
			if (cancellationToken.IsCancellationRequested)
				return new(frameIndex, velocityChecksum, [], true);
			CfdStreamline? line = TraceOne(
				sampler,
				boundary,
				seed,
				stepLength,
				extent.Length() * 2.5f,
				maximumSpeed,
				cancellationToken);
			if (cancellationToken.IsCancellationRequested)
				return new(frameIndex, velocityChecksum, [], true);
			if (line != null) result.Add(line);
		}
		return new(frameIndex, velocityChecksum, result.ToArray());
	}

	private static CfdStreamline? TraceOne(
		CfdVelocityFrameSampler sampler,
		CfdBoundaryBvh? boundary,
		CfdStreamlineSeed seed,
		float stepLength,
		float maximumLength,
		double maximumSpeed,
		CancellationToken cancellationToken)
	{
		Vector3 position = seed.Position;
		if (!sampler.TrySample(position, out Vector3 initialVelocity) || initialVelocity.LengthSquared() <= 1e-12f) return null;
		if (boundary == null) position += Vector3.Normalize(initialVelocity) * stepLength * 0.75f;
		List<CfdStreamlinePoint> points = [];
		float tracedLength = 0;
		CfdStreamlineTermination termination = CfdStreamlineTermination.TraceLimit;
		for (int step = 0; step < 720 && tracedLength < maximumLength; ++step)
		{
			if ((step & 15) == 0 && cancellationToken.IsCancellationRequested) return null;
			if (!sampler.TrySample(position, out Vector3 firstVelocity))
			{
				termination = CfdStreamlineTermination.SampleSupport;
				break;
			}
			double speed = firstVelocity.Length();
			if (speed <= maximumSpeed * 1e-4)
			{
				termination = CfdStreamlineTermination.LowSpeed;
				break;
			}
			points.Add(new(position, firstVelocity, speed));
			Vector3 midpoint = position + Vector3.Normalize(firstVelocity) * (stepLength * 0.5f);
			if (!sampler.TrySample(midpoint, out Vector3 midpointVelocity) || midpointVelocity.LengthSquared() <= 1e-12f)
			{
				termination = CfdStreamlineTermination.SampleSupport;
				break;
			}
			Vector3 next = position + Vector3.Normalize(midpointVelocity) * stepLength;
			if (boundary != null && boundary.TryIntersectSegment(position, next, out CfdBoundaryHit hit))
			{
				if (hit.Role == "outlet")
				{
					points.Add(new(hit.Position, midpointVelocity, midpointVelocity.Length()));
					termination = CfdStreamlineTermination.Outlet;
					break;
				}
				if (hit.Role != "walls" || !TryFollowWall(
					boundary,
					position,
					midpointVelocity,
					hit,
					stepLength,
					out next))
				{
					termination = hit.Role == "inlet"
						? CfdStreamlineTermination.Inlet
						: CfdStreamlineTermination.Wall;
					break;
				}
			}
			if (boundary != null && !boundary.IsInside(next))
			{
				termination = CfdStreamlineTermination.Wall;
				break;
			}
			if (points.Count > 32 && points.Take(points.Count - 24).Any(value =>
				Vector3.DistanceSquared(value.Position, next) < stepLength * stepLength * 0.3f))
			{
				termination = CfdStreamlineTermination.Loop;
				break;
			}
			position = next;
			tracedLength += stepLength;
		}
		return points.Count >= 4 ? new(points.ToArray(), termination) : null;
	}

	private static bool TryFollowWall(
		CfdBoundaryBvh boundary,
		Vector3 position,
		Vector3 velocity,
		CfdBoundaryHit wall,
		float stepLength,
		out Vector3 next)
	{
		Vector3 tangent = velocity - wall.Normal * Vector3.Dot(velocity, wall.Normal);
		if (tangent.LengthSquared() <= velocity.LengthSquared() * 1e-6f)
		{
			next = default;
			return false;
		}
		Vector3 direction = Vector3.Normalize(tangent);
		float clearance = MathF.Max(boundary.Epsilon * 8, stepLength * 0.08f);
		Vector3 positive = wall.Position + wall.Normal * clearance;
		Vector3 inward = boundary.IsInside(positive) ? wall.Normal : -wall.Normal;
		for (int inwardAttempt = 0; inwardAttempt < 4; ++inwardAttempt)
		{
			Vector3 anchor = position + inward * clearance * (1 << inwardAttempt);
			float candidateLength = stepLength;
			for (int attempt = 0; attempt < 6; ++attempt)
			{
				Vector3 candidate = anchor + direction * candidateLength;
				if (!boundary.TryIntersectSegment(position, candidate, out _)
					&& boundary.IsInside(candidate))
				{
					next = candidate;
					return true;
				}
				candidateLength *= 0.5f;
			}
		}
		next = default;
		return false;
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
	private static IReadOnlyList<CfdStreamlineSeed> SelectActiveInletSeeds(
		IReadOnlyList<CfdBoundaryPatch> boundaries,
		CfdBoundaryBvh boundary,
		int seedsPerInlet)
	{
		List<CfdStreamlineSeed> result = [];
		foreach (CfdBoundaryPatch inlet in boundaries.Where(value => value.Role == "inlet")
			.OrderBy(value => value.Name, StringComparer.Ordinal))
		{
			if (!inlet.Data.PointVectors.TryGetValue("U", out VtkVector[]? field) || field.Length == 0) continue;
			Vector3 velocity = field.Select(CfdSpatialSampleIndex.Point)
				.Aggregate(Vector3.Zero, (sum, value) => sum + value) / field.Length;
			if (velocity.LengthSquared() <= 1e-8f) continue;
			Vector3 direction = Vector3.Normalize(velocity);
			foreach (Vector3 surfaceSeed in SelectInletSeedPoints(inlet.Data, seedsPerInlet))
			{
				float offset = boundary.Epsilon;
				bool accepted = false;
				for (int attempt = 0; attempt < 8; ++attempt)
				{
					Vector3 candidate = surfaceSeed + direction * offset;
					if (boundary.IsInside(candidate))
					{
						result.Add(new(candidate, direction, inlet.Name));
						accepted = true;
						break;
					}
					offset *= 2;
				}
				if (!accepted) continue;
			}
		}
		return result;
	}

	private static IReadOnlyList<Vector3> SelectInletSeedPoints(LegacyVtkDataSet inlet, int count)
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
