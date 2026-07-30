using System.Diagnostics;
using System.Numerics;
using FishGfx.Cad;
using FishGfx.Game;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;

namespace FishGfx.CFD;

internal sealed partial class CfdViewerApplication : IDisposable
{
	private static readonly string[] Fields = ["p", "T", "U", "rho", "Ma", "yPlus"];
	private readonly RenderWindow window;
	private readonly InputManager input;
	private readonly Camera camera = new();
	private readonly Camera uiCamera = new();
	private readonly Mesh3D? surface;
	private readonly Mesh3D? wireframe;
	private readonly Mesh3D? flowContext;
	private readonly Mesh3D? slice;
	private RenderTarget? sceneTarget;
	private readonly List<VelocityArrow> arrows = [];
	private readonly List<CfdStreamline> streamlines = [];
	private readonly CfdStreamlineCache streamlineCache = new();
	private CfdSpatialSampleIndex? spatialSampleIndex;
	private CfdBoundaryBvh? boundaryBvh;
	private IReadOnlyList<CfdBoundaryPatch> currentBoundaries = [];
	private IReadOnlyList<CfdStreamlineSeed> streamlineSeeds = [];
	private Task<CfdStreamlineResult>? pendingStreamlineLoad;
	private CancellationTokenSource? streamlineLoadCancellation;
	private int streamlineDueFrame = -1;
	private string streamlineDueChecksum = string.Empty;
	private long streamlineDueTimestamp;
	private int displayedStreamlineFrame;
	private readonly CfdViewerUi ui;
	private readonly ICfdResultSequence? resultSequence;
	private LegacyVtkDataSet? volume;
	private LegacyVtkDataSet? walls;
	private readonly Vector3[] surfaceVertices = [];
	private readonly Vector3[] surfaceNormals = [];
	private readonly uint[] surfaceIndices = [];
	private bool showSurface = true;
	private bool showWireframe;
	private bool showSlice;
	private bool showArrows;
	private bool showStreamlines;
	private string mode = "Surface";
	private string field = "p";
	private int[] slicePointIndices = [];
	private int slicePointCount;
	private float slicePlane;
	private double velocityMaximum;
	private CfdPickedValue? pickedValue;
	private Vector3 orbitTarget;
	private float orbitDistance;
	private float orbitYaw;
	private float orbitPitch;
	private Vector2 previousOrbitMouse;
	private bool orbiting;
	private int currentFrameIndex;
	private int requestedFrameIndex;
	private Task<CfdResultFrame>? pendingFrameLoad;
	private CancellationTokenSource? frameLoadCancellation;
	private bool playing;
	private double playbackAccumulator;
	private bool disposed;

	internal CfdViewerApplication(
		CadTessellation? tessellation,
		VerifiedOpenFoamResults? results = null,
		CfdResultSummary? summary = null)
		: this(
			tessellation,
			results == null ? null : new CfdSteadyResultSequence(results),
			summary)
	{
	}

	internal CfdViewerApplication(
		ICfdResultSequence sequence,
		CfdResultSummary? summary = null,
		CfdEngineTransientSettings? transient = null,
		CfdTransientResultReference? transientResult = null,
		CfdTransientResultSummary? transientSummary = null)
		: this(null, sequence, summary, transient, transientResult, transientSummary)
	{
	}

	private CfdViewerApplication(
		CadTessellation? tessellation,
		ICfdResultSequence? sequence,
		CfdResultSummary? summary,
		CfdEngineTransientSettings? transient = null,
		CfdTransientResultReference? transientResult = null,
		CfdTransientResultSummary? transientSummary = null)
	{
		resultSequence = sequence;
		VerifiedOpenFoamResults? results = sequence?.LoadFrameAsync(0, CancellationToken.None)
			.AsTask().GetAwaiter().GetResult().Results;
		string title = sequence == null
			? "FishGfx.CFD — Published Gas Geometry"
			: sequence.AnalysisMode == CfdAnalysisMode.EngineTransient
				? "FishGfx.CFD — Engine Transient Results"
				: "FishGfx.CFD — Steady Compressible Results";
		window = new RenderWindow(1280, 800, title, true);
		uiCamera.SetOrthogonal(0, 0, window.Width, window.Height);
		window.Resized += (_, args) => uiCamera.SetOrthogonal(0, 0, args.Width, args.Height);
		input = new InputManager(window);
		volume = results?.Volume;
		currentBoundaries = results?.Boundaries ?? [];
		walls = results?.Boundaries.FirstOrDefault(item => item.Role == "walls")?.Data;
		if (walls != null)
		{
			(surfaceVertices, surfaceNormals, surfaceIndices) = BoundaryMesh(walls);
		}
		else if (tessellation != null)
		{
			surfaceVertices = tessellation.Vertices.Select(vertex =>
				new Vector3(vertex.X, vertex.Y, vertex.Z)).ToArray();
			surfaceNormals = tessellation.Vertices.Select(vertex =>
				new Vector3(vertex.NormalX, vertex.NormalY, vertex.NormalZ)).ToArray();
			surfaceIndices = tessellation.Indices;
		}
		if (surfaceVertices.Length > 0)
		{
			surface = CreateMesh(surfaceVertices, surfaceNormals, surfaceIndices,
				FieldColors(walls, field, surfaceVertices.Length, "walls"));
			wireframe = CreateMesh(
				surfaceVertices,
				surfaceNormals,
				surfaceIndices,
				Enumerable.Repeat(new Color(225, 235, 245), surfaceVertices.Length).ToArray());
			wireframe.PolygonMode = PolygonMode.Line;
			flowContext = CreateMesh(
				surfaceVertices,
				surfaceNormals,
				surfaceIndices,
				Enumerable.Repeat(new Color(72, 92, 108, 38), surfaceVertices.Length).ToArray());
		}
		if (volume != null)
		{
			slice = window.Graphics.CreateMesh3D(BufferUsage.Dynamic);
			slice.PrimitiveType = PrimitiveType.Points;
			BuildSlice();
			BuildArrows();
			spatialSampleIndex = new(volume.Points);
			if (currentBoundaries.Count > 0)
			{
				boundaryBvh = new(currentBoundaries, spatialSampleIndex.CellSize);
				streamlineSeeds = SelectActiveInletSeeds(currentBoundaries, boundaryBvh, 5);
			}
			ScheduleStreamlines(0, sequence?.GetFrameInfo(0).VelocityBlockChecksum ?? "steady", true);
		}
		FitCamera();
		ui = new CfdViewerUi(window, summary, Fields, sequence, transient, transientResult, transientSummary);
		ui.ModeRequested += mode =>
		{
			this.mode = mode;
			showSurface = mode == "Surface";
			showWireframe = mode == "Mesh";
			showSlice = mode == "Slice";
			showArrows = mode == "Velocity";
			showStreamlines = mode == "Streamlines";
			pickedValue = null;
			if (showStreamlines && resultSequence != null)
				ScheduleStreamlines(currentFrameIndex, resultSequence.GetFrameInfo(currentFrameIndex).VelocityBlockChecksum, true);
			RefreshLegend();
		};
		ui.FieldRequested += selected =>
		{
			field = selected;
			if (surface != null) surface.SetColors(FieldColors(walls, field, surfaceVertices.Length, "walls"));
			BuildSlice();
			pickedValue = null;
			RefreshLegend();
		};
		ui.FrameRequested += RequestFrame;
		ui.PlayPauseRequested += () => playing = !playing;
		ui.StepRequested += amount =>
		{
			playing = false;
			RequestFrame(WrapFrame(currentFrameIndex + amount));
		};
		RefreshLegend();
		UpdateTimeline(false);
	}

	internal void Run()
	{
		Stopwatch timing = Stopwatch.StartNew();
		bool automatic = string.Equals(
			Environment.GetEnvironmentVariable("FISHGFX_CFD_AUTO"),
			"1",
			StringComparison.Ordinal);
		bool automaticComplete = false;
		while (!window.IsCloseRequested && !automaticComplete)
		{
			input.BeginFrame();
			ui.BeginFrame();
			window.PollEvents();
			UpdateCameraInteraction();
			UpdatePickingInteraction();
			UpdateResultSequence(1f / 60f);
			ui.Update(1f / 60f, (float)timing.Elapsed.TotalSeconds);
			EnsureSceneTarget();
			using RenderFrame frame = window.Graphics.BeginFrame();
			using (RenderPass pass = frame.BeginPass(sceneTarget!, new RenderPassDescriptor
			{
				View = new RenderView(camera),
				State = RenderState.Default with
				{
					CullMode = CullMode.None,
					DepthTestEnabled = true,
					DepthWriteEnabled = true,
					PointSize = 4,
				},
				ColorLoadAction = RenderLoadAction.Clear,
				DepthLoadAction = RenderLoadAction.Clear,
				StencilLoadAction = RenderLoadAction.Clear,
				ClearColor = new Color(16, 20, 26),
			}))
			{
				if (showSurface && surface is not null) pass.DrawMesh(surface);
				if (showWireframe && wireframe is not null) pass.DrawMesh(wireframe);
				if (showSlice && slice is not null) pass.DrawMesh(slice);
				if (showArrows)
					DrawVelocityArrows(pass);
				if (showStreamlines)
					DrawStreamlines(pass);
				if (pickedValue != null)
					pass.DrawPoint(new Vertex3(pickedValue.Position, Color.Yellow), 11);
			}
			if ((showArrows || showStreamlines) && flowContext is not null)
			{
				using RenderPass contextPass = frame.BeginPass(sceneTarget!, new RenderPassDescriptor
				{
					View = new RenderView(camera),
					State = RenderState.Default with
					{
						CullMode = CullMode.None,
						DepthTestEnabled = true,
						DepthWriteEnabled = false,
					},
					ColorLoadAction = RenderLoadAction.Load,
					DepthLoadAction = RenderLoadAction.Load,
					StencilLoadAction = RenderLoadAction.Load,
				});
				contextPass.DrawMesh(flowContext);
			}
			using (RenderPass uiPass = frame.BeginPass(window.Graphics.Backbuffer, new RenderPassDescriptor
			{
				View = new RenderView(uiCamera),
				State = RenderState.Default with
				{
					CullMode = CullMode.None,
					DepthTestEnabled = false,
					DepthWriteEnabled = false,
				},
				ColorLoadAction = RenderLoadAction.Clear,
				DepthLoadAction = RenderLoadAction.Clear,
				StencilLoadAction = RenderLoadAction.Clear,
				ClearColor = new Color(16, 20, 26),
			}))
			{
				uiPass.DrawTexturedRectangle(
					0,
					0,
					window.Width,
					window.Height,
					0,
					1,
					1,
					0,
					Color.White,
					sceneTarget!.ColorAttachments[0]);
				ui.Render(uiPass, 1f / 60f, (float)timing.Elapsed.TotalSeconds);
			}
			frame.Present();
			if (automatic && timing.Elapsed.TotalSeconds > 1.5)
			{
				string screenshotPath = Path.GetFullPath(
					Environment.GetEnvironmentVariable("FISHGFX_CFD_AUTO_SCREENSHOT")
						?? Path.Combine(Path.GetTempPath(), "fishgfx-cfd-auto.png"));
				Directory.CreateDirectory(Path.GetDirectoryName(screenshotPath)!);
				CaptureScreenshot(screenshotPath);
				Console.WriteLine(
					$"FISHGFX_CFD_AUTO_OK field={field} surfaceVertices={surfaceVertices.Length} "
						+ $"slicePoints={slicePointCount} arrows={arrows.Count} streamlines={streamlines.Count} "
						+ $"pick={pickedValue?.PrimaryText ?? "none"} screenshot={screenshotPath}");
				automaticComplete = true;
			}
		}
	}

	private void UpdateResultSequence(float deltaTime)
	{
		UpdateStreamlines();
		if (resultSequence == null || resultSequence.FrameCount <= 1) return;
		if (pendingFrameLoad is { IsCompleted: true } pending)
		{
			pendingFrameLoad = null;
			if (pending.IsCompletedSuccessfully)
			{
				CfdResultFrame frame = pending.Result;
				CfdFrameInfo requested = resultSequence.GetFrameInfo(requestedFrameIndex);
				if (frame.Info.Index == requestedFrameIndex
					&& frame.Info.VelocityBlockChecksum == requested.VelocityBlockChecksum)
				{
					ApplyFrame(frame);
				}
			}
			else if (!pending.IsCanceled)
			{
				throw pending.Exception?.GetBaseException() ?? new InvalidDataException("A CFD frame load failed.");
			}
		}
		if (!playing || pendingFrameLoad != null) return;
		playbackAccumulator += deltaTime;
		const double frameInterval = 1.0 / 30.0;
		if (playbackAccumulator >= frameInterval)
		{
			playbackAccumulator %= frameInterval;
			RequestFrame(WrapFrame(currentFrameIndex + 1));
		}
	}

	private void RequestFrame(int index)
	{
		if (resultSequence == null || resultSequence.FrameCount <= 1) return;
		index = WrapFrame(index);
		if (index == currentFrameIndex && pendingFrameLoad == null) return;
		frameLoadCancellation?.Cancel();
		frameLoadCancellation?.Dispose();
		frameLoadCancellation = new CancellationTokenSource();
		requestedFrameIndex = index;
		pendingFrameLoad = resultSequence.LoadFrameAsync(index, frameLoadCancellation.Token).AsTask();
		UpdateTimeline(true);
	}

	private void ApplyFrame(CfdResultFrame frame)
	{
		volume = frame.Results.Volume;
		currentBoundaries = frame.Results.Boundaries;
		walls = frame.Results.Boundaries.FirstOrDefault(value => value.Role == "walls")?.Data;
		if (surface != null) surface.SetColors(FieldColors(walls, field, surfaceVertices.Length, "walls"));
		BuildSlice();
		arrows.Clear();
		BuildArrows();
		currentFrameIndex = frame.Info.Index;
		if (boundaryBvh != null)
			streamlineSeeds = SelectActiveInletSeeds(currentBoundaries, boundaryBvh, 5);
		ScheduleStreamlines(frame.Info.Index, frame.Info.VelocityBlockChecksum, !playing);
		pickedValue = null;
		RefreshLegend();
		UpdateTimeline(false);
	}

	private void ScheduleStreamlines(int frame, string checksum, bool exact)
	{
		if (spatialSampleIndex == null || volume == null || !volume.PointVectors.ContainsKey("U")) return;
		if (!exact && frame % 10 != 0) return;
		if (streamlineCache.TryGet(frame, checksum, out CfdStreamline[] cached))
		{
			streamlines.Clear();
			streamlines.AddRange(cached);
			displayedStreamlineFrame = frame;
			return;
		}
		if (pendingStreamlineLoad != null)
			streamlineLoadCancellation?.Cancel();
		streamlineDueFrame = frame;
		streamlineDueChecksum = checksum;
		streamlineDueTimestamp = Stopwatch.GetTimestamp() + (exact
			? (long)(Stopwatch.Frequency * 0.15)
			: 0);
	}

	private void UpdateStreamlines()
	{
		if (pendingStreamlineLoad is { IsCompleted: true } pending)
		{
			pendingStreamlineLoad = null;
			if (pending.IsCompletedSuccessfully)
			{
				CfdStreamlineResult result = pending.Result;
				if (!result.IsCanceled)
				{
					streamlineCache.Add(result);
					if (resultSequence != null)
					{
						CfdFrameInfo active = resultSequence.GetFrameInfo(currentFrameIndex);
						if (result.FrameIndex == active.Index && result.VelocityChecksum == active.VelocityBlockChecksum)
						{
							streamlines.Clear();
							streamlines.AddRange(result.Lines);
							displayedStreamlineFrame = result.FrameIndex;
							RefreshLegend();
						}
					}
				}
			}
			else if (!pending.IsCanceled) throw pending.Exception?.GetBaseException()
				?? new InvalidDataException("Streamline generation failed.");
		}
		if (pendingStreamlineLoad != null || streamlineDueFrame < 0 || Stopwatch.GetTimestamp() < streamlineDueTimestamp) return;
		if (volume == null || spatialSampleIndex == null || !volume.PointVectors.TryGetValue("U", out VtkVector[]? velocities)) return;
		int frame = streamlineDueFrame;
		string checksum = streamlineDueChecksum;
		streamlineDueFrame = -1;
		streamlineLoadCancellation?.Cancel();
		streamlineLoadCancellation?.Dispose();
		streamlineLoadCancellation = new();
		CancellationToken token = streamlineLoadCancellation.Token;
		pendingStreamlineLoad = Task.Run(() => CfdStreamlineTracer.Trace(
			spatialSampleIndex,
			velocities,
			streamlineSeeds,
			boundaryBvh,
			token,
			frame,
			checksum));
	}

	private int WrapFrame(int index)
	{
		int count = resultSequence?.FrameCount ?? 1;
		int result = index % count;
		return result < 0 ? result + count : result;
	}

	private void UpdateTimeline(bool loading)
	{
		if (resultSequence == null) return;
		ui.SetTimeline(resultSequence.GetFrameInfo(currentFrameIndex), playing, loading);
	}

	private Mesh3D CreateMesh(Vector3[] vertices, Vector3[] normals, uint[] indices, Color[] colors)
	{
		Mesh3D result = window.Graphics.CreateMesh3D(BufferUsage.Dynamic);
		result.SetVertices(vertices);
		result.SetNormals(normals);
		result.SetColors(colors);
		result.SetElements(indices);
		return result;
	}

	private void EnsureSceneTarget()
	{
		if (sceneTarget != null
			&& sceneTarget.Width == window.Width
			&& sceneTarget.Height == window.Height)
		{
			return;
		}
		sceneTarget?.Dispose();
		sceneTarget = window.Graphics.CreateRenderTarget(
			new RenderTargetDescriptor(window.Width, window.Height));
	}

	private void FitCamera()
	{
		Vector3[] points = surfaceVertices.Length > 0
			? surfaceVertices
			: volume?.Points.Select(Point).ToArray() ?? [Vector3.Zero, Vector3.One];
		Vector3 minimum = new(points.Min(item => item.X), points.Min(item => item.Y), points.Min(item => item.Z));
		Vector3 maximum = new(points.Max(item => item.X), points.Max(item => item.Y), points.Max(item => item.Z));
		Vector3 center = (minimum + maximum) / 2;
		float distance = Math.Max((maximum - minimum).Length() * 1.4f, 0.01f);
		Vector3 direction = Vector3.Normalize(new Vector3(1, 0.65f, 1));
		orbitTarget = center;
		orbitDistance = distance;
		orbitYaw = MathF.Atan2(direction.X, direction.Z) * 180 / MathF.PI;
		orbitPitch = MathF.Asin(direction.Y) * 180 / MathF.PI;
		ConfigureCamera();
	}

	private void UpdateCameraInteraction()
	{
		Vector2 mouse = window.MousePosition;
		if (input.WasMouseButtonPressed(MouseButton.Right))
		{
			orbiting = !ui.IsPointerOverControls(mouse);
			previousOrbitMouse = mouse;
		}

		if (orbiting && input.IsMouseButtonDown(MouseButton.Right))
		{
			Vector2 delta = mouse - previousOrbitMouse;
			orbitYaw -= delta.X * 0.35f;
			orbitPitch = Math.Clamp(orbitPitch - delta.Y * 0.35f, -89, 89);
		}

		if (input.WasMouseButtonReleased(MouseButton.Right)) orbiting = false;
		previousOrbitMouse = mouse;
		ConfigureCamera();
	}

	private void ConfigureCamera()
	{
		float yaw = orbitYaw * MathF.PI / 180;
		float pitch = orbitPitch * MathF.PI / 180;
		Vector3 direction = new(
			MathF.Sin(yaw) * MathF.Cos(pitch),
			MathF.Sin(pitch),
			MathF.Cos(yaw) * MathF.Cos(pitch));
		camera.Position = orbitTarget + direction * orbitDistance;
		camera.CameraUpNormal = Vector3.UnitY;
		camera.LookAt(orbitTarget);
		camera.SetPerspective(
			Math.Max(window.Width, 1),
			Math.Max(window.Height, 1),
			MathF.PI / 3,
			orbitDistance / 1000,
			orbitDistance * 20);
	}

	private void BuildSlice()
	{
		if (volume == null || slice == null) return;
		float minimum = (float)volume.Points.Min(item => item.Z);
		float maximum = (float)volume.Points.Max(item => item.Z);
		float plane = (minimum + maximum) / 2;
		slicePlane = plane;
		float thickness = Math.Max((maximum - minimum) * 0.006f, 1e-6f);
		int stride = Math.Max(1, volume.Points.Length / 30000);
		List<int> selected = [];
		for (int index = 0; index < volume.Points.Length; index += stride)
			if (Math.Abs(volume.Points[index].Z - plane) <= thickness) selected.Add(index);
		slicePointIndices = selected.ToArray();
		Vector3[] points = slicePointIndices.Select(index => Point(volume.Points[index])).ToArray();
		slicePointCount = points.Length;
		slice.SetVertices(points);
		slice.SetColors(SelectedPointColors(volume, field, selected, "volume"));
		slice.SetElements([]);
	}

	private void BuildArrows()
	{
		if (volume == null || !volume.PointVectors.TryGetValue("U", out VtkVector[]? velocity)) return;
		double maximum = resultSequence is ICfdFieldRangeProvider ranges
			&& ranges.TryGetRange("U", "volume", out CfdFieldRange cycleRange)
			? cycleRange.Maximum
			: velocity.Max(item => item.Length);
		if (!(maximum > 0)) return;
		velocityMaximum = maximum;
		Vector3 minimum = new(
			(float)volume.Points.Min(item => item.X),
			(float)volume.Points.Min(item => item.Y),
			(float)volume.Points.Min(item => item.Z));
		Vector3 maximumPoint = new(
			(float)volume.Points.Max(item => item.X),
			(float)volume.Points.Max(item => item.Y),
			(float)volume.Points.Max(item => item.Z));
		Vector3 extent = maximumPoint - minimum;
		const int divisions = 8;
		Dictionary<int, (int Index, float CenterDistance)> selected = [];
		for (int index = 0; index < volume.Points.Length; ++index)
		{
			Vector3 position = Point(volume.Points[index]);
			Vector3 normalized = new(
				extent.X > 0 ? (position.X - minimum.X) / extent.X : 0,
				extent.Y > 0 ? (position.Y - minimum.Y) / extent.Y : 0,
				extent.Z > 0 ? (position.Z - minimum.Z) / extent.Z : 0);
			int x = Math.Clamp((int)(normalized.X * divisions), 0, divisions - 1);
			int y = Math.Clamp((int)(normalized.Y * divisions), 0, divisions - 1);
			int z = Math.Clamp((int)(normalized.Z * divisions), 0, divisions - 1);
			int key = x + divisions * (y + divisions * z);
			Vector3 center = minimum + new Vector3(
				(x + 0.5f) / divisions * extent.X,
				(y + 0.5f) / divisions * extent.Y,
				(z + 0.5f) / divisions * extent.Z);
			float distance = Vector3.DistanceSquared(position, center);
			if (!selected.TryGetValue(key, out var current) || distance < current.CenterDistance)
				selected[key] = (index, distance);
		}
		float baseLength = extent.Length();
		foreach (int index in selected.Values.Select(item => item.Index).Order())
		{
			double speed = velocity[index].Length;
			if (speed <= maximum * 0.002) continue;
			Vector3 start = Point(volume.Points[index]);
			Vector3 direction = Vector3.Normalize(Point(velocity[index]));
			float normalizedSpeed = (float)Math.Clamp(speed / maximum, 0, 1);
			float length = baseLength * (0.02f + 0.06f * MathF.Sqrt(normalizedSpeed));
			arrows.Add(new VelocityArrow(
				start,
				start + direction * length,
				FieldColor(normalizedSpeed),
				index));
		}
	}

	private static (Vector3[] Vertices, Vector3[] Normals, uint[] Indices) BoundaryMesh(LegacyVtkDataSet data)
	{
		Vector3[] vertices = data.Points.Select(Point).ToArray();
		Vector3[] normals = new Vector3[vertices.Length];
		List<uint> indices = [];
		foreach (VtkCell cell in data.Cells)
		{
			for (int index = 1; index + 1 < cell.PointIndices.Length; ++index)
			{
				int a = cell.PointIndices[0];
				int b = cell.PointIndices[index];
				int c = cell.PointIndices[index + 1];
				indices.Add((uint)a); indices.Add((uint)b); indices.Add((uint)c);
				Vector3 normal = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
				normals[a] += normal; normals[b] += normal; normals[c] += normal;
			}
		}
		for (int index = 0; index < normals.Length; ++index)
			normals[index] = normals[index].LengthSquared() > 0 ? Vector3.Normalize(normals[index]) : Vector3.UnitZ;
		return (vertices, normals, indices.ToArray());
	}

	private Color[] FieldColors(
		LegacyVtkDataSet? data,
		string name,
		int count,
		string association)
	{
		if (data == null) return Enumerable.Repeat(new Color(70, 165, 220), count).ToArray();
		double[] values = PointValues(data, name);
		return Colors(values, count, GlobalRange(name, association));
	}

	private Color[] SelectedPointColors(
		LegacyVtkDataSet data,
		string name,
		List<int> selected,
		string association)
	{
		double[] all = PointValues(data, name);
		return Colors(selected.Select(index => all[index]).ToArray(), selected.Count, GlobalRange(name, association));
	}

	private static double[] PointValues(LegacyVtkDataSet data, string name)
	{
		if (name == "U" && data.PointVectors.TryGetValue(name, out VtkVector[]? vectors))
			return vectors.Select(item => item.Length).ToArray();
		if (data.PointScalars.TryGetValue(name, out double[]? values)) return values;
		return new double[data.Points.Length];
	}

	private static Color[] Colors(double[] values, int count, CfdFieldRange? fixedRange)
	{
		if (values.Length == 0) return [];
		double minimum = fixedRange?.Minimum ?? values.Where(double.IsFinite).DefaultIfEmpty(0).Min();
		double maximum = fixedRange?.Maximum ?? values.Where(double.IsFinite).DefaultIfEmpty(1).Max();
		double range = Math.Max(maximum - minimum, double.Epsilon);
		return Enumerable.Range(0, count).Select(index =>
		{
			double t = Math.Clamp((values[index] - minimum) / range, 0, 1);
			return FieldColor(t);
		}).ToArray();
	}

	private CfdFieldRange? GlobalRange(string name, string association) =>
		resultSequence is ICfdFieldRangeProvider provider
		&& provider.TryGetRange(name, association, out CfdFieldRange range)
			? range
			: null;

	private static Color FieldColor(double normalized)
	{
		double value = Math.Clamp(normalized, 0, 1);
		return new Color(
			(byte)(30 + 225 * value),
			(byte)(80 + 140 * (1 - Math.Abs(2 * value - 1))),
			(byte)(240 - 210 * value));
	}

	private static Vector3 Point(VtkVector value) => new((float)value.X, (float)value.Y, (float)value.Z);

	private unsafe void CaptureScreenshot(string path)
	{
		window.ReadPixels();
		using System.Drawing.Bitmap bitmap = new(
			window.Width,
			window.Height,
			System.Drawing.Imaging.PixelFormat.Format32bppArgb);
		System.Drawing.Rectangle rectangle = new(0, 0, bitmap.Width, bitmap.Height);
		System.Drawing.Imaging.BitmapData data = bitmap.LockBits(
			rectangle,
			System.Drawing.Imaging.ImageLockMode.WriteOnly,
			System.Drawing.Imaging.PixelFormat.Format32bppArgb);
		try
		{
			for (int y = 0; y < bitmap.Height; ++y)
			for (int x = 0; x < bitmap.Width; ++x)
			{
				Color color = window.GetPixel(x, y);
				byte* pixel = (byte*)data.Scan0 + y * data.Stride + x * 4;
				pixel[0] = color.B;
				pixel[1] = color.G;
				pixel[2] = color.R;
				pixel[3] = color.A;
			}
		}
		finally
		{
			bitmap.UnlockBits(data);
		}
		bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
	}

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		frameLoadCancellation?.Cancel();
		frameLoadCancellation?.Dispose();
		streamlineLoadCancellation?.Cancel();
		streamlineLoadCancellation?.Dispose();
		ui.Dispose();
		surface?.Dispose();
		wireframe?.Dispose();
		flowContext?.Dispose();
		slice?.Dispose();
		sceneTarget?.Dispose();
		input.Dispose();
		window.Graphics.CollectGarbage();
		window.Dispose();
	}
}
