using System.Numerics;
using FishGfx.Graphics;
using Xunit;

namespace FishGfx.CFD.Tests;

public sealed class CfdViewerPickingTests
{
	[Fact]
	public void TrianglePickReturnsDistanceAndBarycentricCoordinates()
	{
		PickingRay ray = new(new Vector3(0.25f, 0.25f, 1), -Vector3.UnitZ);

		bool hit = CfdViewerApplication.TryIntersectTriangle(
			ray,
			Vector3.Zero,
			Vector3.UnitX,
			Vector3.UnitY,
			out float distance,
			out float u,
			out float v);

		Assert.True(hit);
		Assert.Equal(1, distance, 5);
		Assert.Equal(0.25f, u, 5);
		Assert.Equal(0.25f, v, 5);
	}

	[Fact]
	public void TrianglePickRejectsPointOutsideTriangle()
	{
		PickingRay ray = new(new Vector3(0.8f, 0.8f, 1), -Vector3.UnitZ);

		Assert.False(CfdViewerApplication.TryIntersectTriangle(
			ray,
			Vector3.Zero,
			Vector3.UnitX,
			Vector3.UnitY,
			out _,
			out _,
			out _));
	}

	[Fact]
	public void ClosestSegmentAmountClampsToDisplayedArrow()
	{
		Assert.Equal(0, CfdViewerApplication.ClosestSegmentAmount(
			new Vector2(-2, 1),
			Vector2.Zero,
			new Vector2(10, 0)));
		Assert.Equal(0.4f, CfdViewerApplication.ClosestSegmentAmount(
			new Vector2(4, 3),
			Vector2.Zero,
			new Vector2(10, 0)), 5);
		Assert.Equal(1, CfdViewerApplication.ClosestSegmentAmount(
			new Vector2(12, -1),
			Vector2.Zero,
			new Vector2(10, 0)));
	}

	[Fact]
	public void VelocitySamplerInterpolatesAConstantFieldAndRejectsDistantPoints()
	{
		VtkVector[] points =
		[
			new(0, 0, 0),
			new(1, 0, 0),
			new(0, 1, 0),
			new(0, 0, 1),
		];
		VtkVector[] velocities = Enumerable.Repeat(new VtkVector(3, -2, 1), points.Length).ToArray();
		CfdVelocityFieldSampler sampler = new(points, velocities, 1);

		Assert.True(sampler.TrySample(new Vector3(0.2f, 0.2f, 0.2f), out Vector3 sampled));
		Assert.Equal(3, sampled.X, 5);
		Assert.Equal(-2, sampled.Y, 5);
		Assert.Equal(1, sampled.Z, 5);
		Assert.False(sampler.TrySample(new Vector3(20, 20, 20), out _));
	}
}
