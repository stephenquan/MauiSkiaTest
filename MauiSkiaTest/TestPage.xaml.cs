// TestPage.xaml.cs

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace MauiSkiaTest;

/// <summary>
/// Represents a test content page.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
	Justification = "The page cancels and disposes its cancellation source when it unloads.")]
public partial class TestPage : ContentPage, IQueryAttributable
{
	const double kRotationPerTick = Math.PI / 180;
	string? assetName;
	IDispatcherTimer? timer;
	CancellationTokenSource? cancellationTokenSource;
	Task renderTask = Task.CompletedTask;
	Task cleanupTask = Task.CompletedTask;
	ObjModel? model;
	readonly ObjModelRenderer renderer = new();
	SKColor backgroundColor;
	bool isLoaded;
	int loadVersion;
	DateTimeOffset previousTick;
	TimeSpan totalElapsed;
	long tickCount;
	double rotation;

	/// <summary>
	/// Initializes a new instance of the <see cref="TestPage"/> class.
	/// </summary>
	public TestPage()
	{
		InitializeComponent();
	}

	/// <summary>
	/// Receives the asset filename from Shell navigation.
	/// </summary>
	/// <param name="query">The navigation parameters.</param>
	public void ApplyQueryAttributes(IDictionary<string, object> query)
	{
		if (!query.TryGetValue("assetName", out var value) || value is not string name || string.IsNullOrWhiteSpace(name))
		{
			throw new ArgumentException("An asset filename is required.", nameof(query));
		}

		assetName = name;
	}

	async void OnLoaded(object? sender, EventArgs e)
	{
		if (isLoaded)
		{
			return;
		}

		string assetName = this.assetName
			?? throw new InvalidOperationException("An asset filename must be supplied before loading the page.");
		cancellationTokenSource = new CancellationTokenSource();
		CancellationToken cancellationToken = cancellationTokenSource.Token;
		isLoaded = true;
		int version = ++loadVersion;
		FrameRateLabel.Text = $"Loading {assetName}...";
		ObjModel loadedModel;
		try
		{
			// A reload must wait until the previous frame and renderer cleanup have finished.
			await cleanupTask;
			cancellationToken.ThrowIfCancellationRequested();
			loadedModel = await Task.Run(() => ObjModel.LoadFromMauiAsset(assetName, cancellationToken), cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return;
		}
		catch (Exception exception) when (exception is IOException or FormatException or UnauthorizedAccessException)
		{
			if (isLoaded && version == loadVersion)
			{
				var logger = Handler?.MauiContext?.Services.GetService<ILogger<TestPage>>();
				logger?.LogError(exception, "Unable to load the asset {AssetName}.", assetName);
				FrameRateLabel.Text = $"Unable to load {assetName}: {exception.Message}";
			}

			return;
		}

		// A previous load must not restart animation after the page unloads or reloads.
		if (!isLoaded || version != loadVersion || cancellationToken.IsCancellationRequested)
		{
			return;
		}

		model = loadedModel;
#if !WINDOWS
		renderer.UseSmoothShading = false;
		renderer.Quality = 4;
#else
		renderer.Quality = 4;
#endif
		var resources = Application.Current?.Resources
			?? throw new InvalidOperationException("Application resources are unavailable.");
		renderer.DefaultColor = ((Color)resources["TeapotOchre"]).ToSKColor();
		backgroundColor = App.Current?.RequestedTheme == AppTheme.Light ? ((Color)resources["Gray100"]).ToSKColor() : ((Color)resources["Gray900"]).ToSKColor();
		rotation = 0;
		tickCount = 0;
		totalElapsed = TimeSpan.Zero;
		FrameRateLabel.Text = $"Waiting for timer";

		timer = Dispatcher.CreateTimer();
		timer.Interval = TimeSpan.FromMilliseconds(1); // TimeSpan.FromSeconds(1.0 / 60);
		timer.IsRepeating = true;
		timer.Tick += OnTick;
		previousTick = DateTimeOffset.UtcNow;
		timer.Start();
		AnimationView.InvalidateSurface();
	}

	async void OnUnloaded(object? sender, EventArgs e)
	{
		if (!isLoaded)
		{
			return;
		}

		isLoaded = false;
		loadVersion++;
		CancellationTokenSource? source = cancellationTokenSource;
		cancellationTokenSource = null;
		CancellationToken cancellationToken = source?.Token ?? CancellationToken.None;
		source?.Cancel();
		if (timer is not null)
		{
			timer.Stop();
			timer.Tick -= OnTick;
			timer = null;
		}

		cleanupTask = CleanupRendererAsync(cleanupTask, renderTask, cancellationToken);
		try
		{
			await cleanupTask;
		}
		finally
		{
			source?.Dispose();
		}
	}

	async Task CleanupRendererAsync(Task previousCleanup, Task previousRender, CancellationToken cancellationToken)
	{
		await previousCleanup;
		try
		{
			await previousRender;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return;
		}
		finally
		{
			renderer.Cleanup();
			model = null;
		}
	}

	async void OnTick(object? sender, EventArgs e)
	{
		if (!isLoaded || !renderTask.IsCompleted || cancellationTokenSource is null ||
			renderer.Bitmap is not SKBitmap bitmap || timer is null || !ReferenceEquals(sender, timer))
		{
			return;
		}

		CancellationToken cancellationToken = cancellationTokenSource.Token;
		int version = loadVersion;
		if (cancellationToken.IsCancellationRequested)
		{
			timer.Stop();
			return;
		}

		var now = DateTimeOffset.UtcNow;
		totalElapsed += now - previousTick;
		previousTick = now;
		tickCount++;

		if (totalElapsed > TimeSpan.Zero)
		{
			double averageMilliseconds = totalElapsed.TotalMilliseconds / tickCount;
			double ticksPerSecond = tickCount / totalElapsed.TotalSeconds;
			// Invalidation can coalesce, so timer throughput is not presented-frame FPS.

			if (averageMilliseconds > 200 && renderer.Quality > 0)
			{
				renderer.Quality--;
			}
			else if (averageMilliseconds < 50 && renderer.Quality < 6)
			{
				renderer.Quality++;
			}

			FrameRateLabel.Text = $"{bitmap.Width}x{bitmap.Height} buffer | {ticksPerSecond:F1} ticks/s | {averageMilliseconds:F2} ms/tick | {Environment.ProcessorCount} cores | Quality: {renderer.Quality}";
		}

		rotation = (rotation + kRotationPerTick) % Math.Tau;
		// Restart after painting, so a new frame cannot prevent the completed frame from being displayed.
		timer.Stop();
		try
		{
			renderTask = RenderBufferAsync(cancellationToken);
			await renderTask;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return;
		}

		if (isLoaded && version == loadVersion && !cancellationToken.IsCancellationRequested)
		{
			AnimationView.InvalidateSurface();
		}
	}

	Task RenderBufferAsync(CancellationToken cancellationToken)
	{
		ObjModel? model = this.model;
		if (model is null || renderer.Bitmap is null)
		{
			return Task.CompletedTask;
		}

		float distance = model.Radius * 3.5f;
		Vector3 viewPosition = model.Center + new Vector3(
			(float)Math.Sin(rotation) * distance,
			model.Radius * 1.2f,
			(float)Math.Cos(rotation) * distance);
		SKColor color = backgroundColor;
		return Task.Run(() => renderer.Render(model, viewPosition, color, cancellationToken), cancellationToken);
	}

	void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
	{
		if (!isLoaded || model is null)
		{
			e.Surface.Canvas.Clear(SKColors.Black);
			return;
		}

		// Painting can resize the renderer, so defer it while a background frame owns its buffers.
		if (renderTask.IsCompleted)
		{
			renderer.PaintSurface(sender, e);
			timer?.Start();
		}
	}
}
