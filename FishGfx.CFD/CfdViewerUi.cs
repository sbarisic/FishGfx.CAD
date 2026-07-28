using System.Numerics;
using FishGfx.FishUI;
using FishGfx.Graphics;
using FishUI.Controls;
using FishUIRuntime = global::FishUI.FishUI;

namespace FishGfx.CFD;

internal sealed class CfdViewerUi : IDisposable
{
	private static readonly Vector2 ControlsPosition = new(10, 10);
	private static readonly Vector2 ControlsSize = new(500, 142);
	private static readonly Vector2 LegendSize = new(410, 260);
	private readonly RenderWindow window;
	private readonly FishUIGraphicsBackend graphics;
	private readonly FishUIInputAdapter input;
	private readonly FishUIRuntime ui;
	private readonly Panel legendPanel;
	private readonly Label legendTitle;
	private readonly Label legendMode;
	private readonly Label legendField;
	private readonly Label legendScaleDescription;
	private readonly CfdLegendScale legendScale;
	private readonly Label legendMinimum;
	private readonly Label legendMidpoint;
	private readonly Label legendMaximum;
	private readonly Label legendPick;
	private readonly Label legendPickDetail;
	private readonly Label legendPickDetail2;

	internal CfdViewerUi(RenderWindow window, CfdResultSummary? summary, IReadOnlyList<string> fields)
	{
		this.window = window;
		graphics = new FishUIGraphicsBackend(window);
		input = new FishUIInputAdapter(window) { Enabled = true };
		global::FishUI.FishUISettings settings = new();
		ui = new FishUIRuntime(settings, graphics, input, new NullFishUIEvents(), graphics.FileSystem);
		ui.Init();
		settings.LoadTheme("data/themes/gwen.yaml");
		Panel panel = new()
		{
			Position = ControlsPosition,
			Size = ControlsSize,
			Variant = PanelVariant.Dark,
		};
		CfdResidualSample? lastResidual = summary?.Residuals.LastOrDefault();
		panel.AddChild(new Label(summary == null
			? "Published gas geometry"
			: $"{summary.Status} | dP0 {summary.PressureLossPa:F1} Pa | imbalance {summary.MassImbalanceFraction:P2} | y+ {summary.YPlusAreaWeightedMean:F2}"
				+ (lastResidual == null ? string.Empty : $" | {lastResidual.Field} r={lastResidual.InitialResidual:E1}"))
		{
			Position = new Vector2(12, 10),
			Size = new Vector2(475, 24),
		});
		string[] modes = ["Surface", "Mesh", "Slice", "Velocity", "Streamlines"];
		for (int index = 0; index < modes.Length; ++index)
		{
			string mode = modes[index];
			Button button = new()
			{
				Position = new Vector2(12 + index * 96, 42),
				Size = new Vector2(92, 28),
				Text = mode,
			};
			button.OnButtonPressed += (_, mouse, _) =>
			{
				if (mouse == global::FishUI.FishMouseButton.Left) ModeRequested?.Invoke(mode);
			};
			panel.AddChild(button);
		}
		for (int index = 0; index < fields.Count; ++index)
		{
			string field = fields[index];
			Button button = new() { Position = new Vector2(12 + index * 66, 82), Size = new Vector2(58, 28), Text = field };
			button.OnButtonPressed += (_, mouse, _) =>
			{
				if (mouse == global::FishUI.FishMouseButton.Left) FieldRequested?.Invoke(field);
			};
			panel.AddChild(button);
		}
		panel.AddChild(new Label("Right-drag the viewport to rotate")
		{
			Position = new Vector2(12, 116),
			Size = new Vector2(475, 18),
		});
		ui.AddControl(panel);

		legendPanel = new Panel
		{
			Position = LegendPosition,
			Size = LegendSize,
			Variant = PanelVariant.Dark,
		};
		legendTitle = AddLegendLabel(legendPanel, 12, 10, 386, "Legend");
		legendMode = AddLegendLabel(legendPanel, 12, 30, 386, string.Empty);
		legendField = AddLegendLabel(legendPanel, 12, 52, 386, string.Empty);
		legendScaleDescription = AddLegendLabel(legendPanel, 12, 72, 386, string.Empty);
		legendScale = new CfdLegendScale
		{
			Position = new Vector2(12, 94),
			Size = new Vector2(386, 16),
		};
		legendPanel.AddChild(legendScale);
		legendMinimum = AddLegendLabel(legendPanel, 12, 112, 125, string.Empty);
		legendMidpoint = AddLegendLabel(legendPanel, 142, 112, 125, string.Empty, Align.Center);
		legendMaximum = AddLegendLabel(legendPanel, 273, 112, 125, string.Empty, Align.Right);
		legendPick = AddLegendLabel(legendPanel, 12, 142, 386, string.Empty);
		legendPickDetail = AddLegendLabel(legendPanel, 12, 164, 386, string.Empty);
		legendPickDetail2 = AddLegendLabel(legendPanel, 12, 186, 386, string.Empty);
		AddLegendLabel(legendPanel, 12, 226, 386, "Left-click data to inspect | Right-drag to rotate");
		ui.AddControl(legendPanel);
	}

	internal event Action<string>? ModeRequested;
	internal event Action<string>? FieldRequested;
	internal bool IsPointerOverControls(Vector2 point) =>
		Contains(point, ControlsPosition, ControlsSize)
		|| Contains(point, LegendPosition, LegendSize);
	internal void SetLegend(CfdLegendState state)
	{
		legendTitle.Text = $"Legend - {state.Mode}";
		legendMode.Text = state.ModeDescription;
		legendField.Text = state.FieldDescription;
		legendScaleDescription.Text = state.ScaleDescription;
		legendScale.Visible = state.ShowColorScale;
		legendMinimum.Text = state.HasRange
			? state.ShowColorScale ? state.Minimum : $"Min: {state.Minimum}"
			: state.Minimum;
		legendMidpoint.Text = state.HasRange && state.ShowColorScale ? state.Midpoint : string.Empty;
		legendMaximum.Text = state.HasRange
			? state.ShowColorScale ? state.Maximum : $"Max: {state.Maximum}"
			: string.Empty;
		legendPick.Text = state.PickText;
		legendPickDetail.Text = state.PickDetail;
		legendPickDetail2.Text = state.PickDetail2;
	}
	internal void BeginFrame() => input.BeginFrame();
	internal void Update(float deltaTime, float time)
	{
		legendPanel.Position = LegendPosition;
		ui.TickUpdate(deltaTime, time);
	}
	internal void Render(RenderPass pass, float deltaTime, float time)
	{
		using (graphics.UseRenderPass(pass, pass.View, pass.State)) ui.TickDraw(deltaTime, time);
	}
	public void Dispose() { input.Dispose(); graphics.Dispose(); }

	private Vector2 LegendPosition => new(Math.Max(10, window.Width - LegendSize.X - 10), 10);

	private static bool Contains(Vector2 point, Vector2 position, Vector2 size) =>
		point.X >= position.X
		&& point.X <= position.X + size.X
		&& point.Y >= position.Y
		&& point.Y <= position.Y + size.Y;

	private static Label AddLegendLabel(
		Panel panel,
		float x,
		float y,
		float width,
		string text,
		Align alignment = Align.Left)
	{
		Label label = new(text)
		{
			Position = new Vector2(x, y),
			Size = new Vector2(width, 18),
			Alignment = alignment,
		};
		panel.AddChild(label);
		return label;
	}
}

internal sealed class CfdLegendScale : Control
{
	public override void DrawControl(global::FishUI.FishUI ui, float deltaTime, float time)
	{
		const int steps = 64;
		Vector2 position = GetAbsolutePosition();
		Vector2 size = GetAbsoluteSize();
		float stepWidth = size.X / steps;
		for (int index = 0; index < steps; ++index)
		{
			double value = index / (double)(steps - 1);
			global::FishUI.FishColor color = new(
				(byte)(30 + 225 * value),
				(byte)(80 + 140 * (1 - Math.Abs(2 * value - 1))),
				(byte)(240 - 210 * value),
				255);
			ui.Graphics.DrawRectangle(
				new Vector2(position.X + index * stepWidth, position.Y),
				new Vector2(stepWidth + 1, size.Y),
				color);
		}
		ui.Graphics.DrawRectangleOutline(position, size, new global::FishUI.FishColor(50, 50, 50, 255));
	}
}
