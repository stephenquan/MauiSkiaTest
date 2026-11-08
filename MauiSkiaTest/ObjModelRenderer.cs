// ObjModelRenderer.cs

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using Triangle = MauiSkiaTest.ObjModel.Triangle;
using Vertex = MauiSkiaTest.ObjModel.Vertex;

namespace MauiSkiaTest;

/// <summary>
/// A lit, depth-buffered bitmap renderer for an <see cref="ObjModel"/>.
/// </summary>
/// <remarks>
/// Owns its bitmap and reuses instance scratch buffers across models.
/// Concurrent rendering, resizing, or cleanup on the same instance is not supported.
/// </remarks>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
	Justification = "Cleanup releases the native bitmap and is called from the page's Unloaded event.")]
public sealed class ObjModelRenderer
{
	static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(-3, 4, 5));
	ProjectedTriangle[] projectedTriangles = [];
	int[] projectedTriangleCounts = [];
	readonly Stopwatch projectionStopwatch = new();
	readonly Stopwatch rasterizationStopwatch = new();
	readonly Stopwatch bitmapCopyStopwatch = new();
	ILogger<ObjModelRenderer>? logger;
	SKBitmap? buffer;
	SKColor[] pixels = [];
	float[] depthBuffer = [];
	Vector4[] transparentColors = [];
	float[] transmittance = [];
	int viewportWidth;
	int viewportHeight;
	int quality = 6;

	/// <summary>The renderer-owned bitmap, or null before resizing or after cleanup.
	/// Callers must not dispose it.</summary>
	public SKBitmap? Bitmap => buffer;

	/// <summary>The rendering quality, from 0 (1/64 viewport resolution) to 6 (full resolution).
	/// Defaults to 5 (half resolution).</summary>
	/// <remarks>Changes take effect on the next resize or render.</remarks>
	public int Quality
	{
		get => quality;
		set
		{
			ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
			ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 6);
			quality = value;
		}
	}

	/// <summary>Whether to interpolate vertex normals for smooth lighting instead of using a constant,
	/// geometrically lit color per face. Defaults to true.</summary>
	public bool UseSmoothShading { get; set; } = true;

	/// <summary>The base color for faces without an MTL diffuse color. Defaults to opaque gray.</summary>
	public SKColor DefaultColor { get; set; } = SKColors.Gray;

	/// <summary>Resizes the owned bitmap for positive viewport dimensions, applying <see cref="Quality"/>.</summary>
	/// <returns>The renderer-owned bitmap, which the caller must not dispose.</returns>
	public SKBitmap Resize(int width, int height)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
		int bitmapWidth = Math.Max(1, width >> (6 - quality));
		int bitmapHeight = Math.Max(1, height >> (6 - quality));
		_ = checked(bitmapWidth * bitmapHeight);
		viewportWidth = width;
		viewportHeight = height;
		if (buffer is not null && buffer.Width == bitmapWidth && buffer.Height == bitmapHeight)
		{
			return buffer;
		}

		var resized = new SKBitmap(bitmapWidth, bitmapHeight);
		if (resized.GetPixels() == IntPtr.Zero)
		{
			resized.Dispose();
			throw new InvalidOperationException($"Unable to allocate a {bitmapWidth}x{bitmapHeight} render bitmap.");
		}

		buffer?.Dispose();
		buffer = resized;
		return resized;
	}

	/// <summary>Releases the owned bitmap and scratch buffers. The renderer can be resized and reused afterward.</summary>
	public void Cleanup()
	{
		buffer?.Dispose();
		buffer = null;
		pixels = [];
		depthBuffer = [];
		transparentColors = [];
		transmittance = [];
		projectedTriangles = [];
		projectedTriangleCounts = [];
		viewportWidth = 0;
		viewportHeight = 0;
		projectionStopwatch.Reset();
		rasterizationStopwatch.Reset();
		bitmapCopyStopwatch.Reset();
	}

	/// <summary>Renders from a world-space camera looking at <see cref="ObjModel.Center"/> with a 45-degree vertical field of view.</summary>
	/// <remarks>Uses Y-up, near-plane clipping, and two-sided lighting.
	/// Transparent faces use weighted order-independent blending against opaque depth.
	/// Overlapping translucent layers are approximated; refraction is not simulated.
	/// Smooth shading uses perspective-correct normal interpolation; flat shading uses geometric face normals.
	/// Projection runs in parallel; rasterization processes opaque faces before transparent faces,
	/// with each triangle's rows processed in parallel.
	/// Call <see cref="Resize"/> before the first render or after cleanup.
	/// Concurrent calls to Render on this instance are not supported.</remarks>
	/// <param name="model">The model to render.</param>
	/// <param name="viewPosition">The world-space camera position.</param>
	/// <param name="background">The background color.</param>
	/// <param name="cancellationToken">Cancels projection, rasterization, and compositing. Defaults to no cancellation.</param>
	public void Render(ObjModel model, Vector3 viewPosition, SKColor background, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(model);
		cancellationToken.ThrowIfCancellationRequested();
		if (buffer is null)
		{
			throw new InvalidOperationException("Resize the renderer before rendering.");
		}

		Vector3 center = model.Center;
		float radius = model.Radius;
		Vector3 camera = (viewPosition - center) / radius;
		if (!IsFinite(camera) || !float.IsFinite(camera.LengthSquared()) || camera.LengthSquared() <= 0)
		{
			throw new ArgumentException("The view position must be finite and different from the mesh center.", nameof(viewPosition));
		}

		projectionStopwatch.Restart();
		SKBitmap scene = Resize(viewportWidth, viewportHeight);
		logger ??= IPlatformApplication.Current?.Services.GetService<ILogger<ObjModelRenderer>>();
		bool smoothShading = UseSmoothShading;
		SKColor defaultColor = DefaultColor;
		Vector3 forward = Vector3.Normalize(-camera);
		Vector3 referenceUp = Math.Abs(forward.Y) > 0.999f ? Vector3.UnitZ : Vector3.UnitY;
		Vector3 right = Vector3.Normalize(Vector3.Cross(forward, referenceUp));
		Vector3 up = Vector3.Cross(right, forward);
		int width = scene.Width;
		int height = scene.Height;
		int triangleCount = model.TriangleCount;
		int pixelCount = checked(width * height);
		if (pixels.Length != pixelCount)
		{
			pixels = new SKColor[pixelCount];
		}
		if (depthBuffer.Length != pixelCount)
		{
			depthBuffer = new float[pixelCount];
		}
		int projectedCapacity = checked(triangleCount * 2);
		if (projectedTriangles.Length < projectedCapacity)
		{
			projectedTriangles = new ProjectedTriangle[projectedCapacity];
		}
		if (projectedTriangleCounts.Length < triangleCount)
		{
			projectedTriangleCounts = new int[triangleCount];
		}

		Array.Fill(pixels, new SKColor(background.Red, background.Green, background.Blue));
		Array.Clear(depthBuffer);
		bool hasTransparency = model.HasTransparency;
		if (hasTransparency)
		{
			if (transparentColors.Length != pixelCount)
			{
				transparentColors = new Vector4[pixelCount];
				transmittance = new float[pixelCount];
			}

			Array.Clear(transparentColors);
			Array.Fill(transmittance, 1f);
		}

		ParallelOptions parallelOptions = new() { CancellationToken = cancellationToken };
		float focalLength = height / (2 * MathF.Tan(MathF.PI / 8));
		Parallel.For(0, triangleCount, parallelOptions, triangleIndex =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			Span<Vertex> input = stackalloc Vertex[3];
			Span<Vertex> clipped = stackalloc Vertex[4];
			Triangle triangle = model.Triangles[triangleIndex];
			SKColor color = triangle.DiffuseColor is Vector3 diffuseColor
				? new SKColor((byte)MathF.Round(diffuseColor.X * 255),
					(byte)MathF.Round(diffuseColor.Y * 255), (byte)MathF.Round(diffuseColor.Z * 255))
				: defaultColor;
			input[0] = ToCamera(triangle.A, camera, right, up, forward, center, radius);
			input[1] = ToCamera(triangle.B, camera, right, up, forward, center, radius);
			input[2] = ToCamera(triangle.C, camera, right, up, forward, center, radius);
			int count = ClipNearPlane(input, clipped);
			// Each source triangle owns two slots; counts exclude unused outputs from previous frames.
			projectedTriangleCounts[triangleIndex] = Math.Max(0, count - 2);
			for (int i = 1; i < count - 1; i++)
			{
				Vector3 faceNormal = Vector3.Cross(clipped[i].Position - clipped[0].Position,
					clipped[i + 1].Position - clipped[0].Position);
				float normalSign = Vector3.Dot(faceNormal, clipped[0].Position) > 0 ? -1 : 1;
				SKColor flatColor = color;
				if (!smoothShading)
				{
					float diffuse = Math.Max(0, Vector3.Dot(triangle.FaceNormal, LightDirection) * normalSign);
					float intensity = 0.25f + 0.75f * diffuse;
					flatColor = new SKColor((byte)(color.Red * intensity),
						(byte)(color.Green * intensity), (byte)(color.Blue * intensity));
				}

				projectedTriangles[triangleIndex * 2 + i - 1] = PrepareTriangle(
					Project(clipped[0], width, height, focalLength, normalSign),
					Project(clipped[i], width, height, focalLength, normalSign),
					Project(clipped[i + 1], width, height, focalLength, normalSign), width, height, color, flatColor,
					triangle.Opacity);
			}
		});
		projectionStopwatch.Stop();

		rasterizationStopwatch.Restart();
		for (int i = 0; i < triangleCount; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			for (int j = 0; j < projectedTriangleCounts[i]; j++)
			{
				ref readonly ProjectedTriangle triangle = ref projectedTriangles[i * 2 + j];
				if (triangle.Opacity == 1)
				{
					Rasterize(in triangle, width, smoothShading, parallelOptions);
				}
			}
		}

		if (hasTransparency)
		{
			for (int i = 0; i < triangleCount; i++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				for (int j = 0; j < projectedTriangleCounts[i]; j++)
				{
					ref readonly ProjectedTriangle triangle = ref projectedTriangles[i * 2 + j];
					if (triangle.Opacity > 0 && triangle.Opacity < 1)
					{
						Rasterize(in triangle, width, smoothShading, parallelOptions);
					}
				}
			}

			Parallel.For(0, pixelCount, parallelOptions, index =>
			{
				Vector4 accumulated = transparentColors[index];
				if (accumulated.W > 0)
				{
					SKColor opaque = pixels[index];
					float transmission = transmittance[index];
					float scale = (1 - transmission) / accumulated.W;
					pixels[index] = new SKColor(
						(byte)Math.Clamp(accumulated.X * scale + opaque.Red * transmission, 0, 255),
						(byte)Math.Clamp(accumulated.Y * scale + opaque.Green * transmission, 0, 255),
						(byte)Math.Clamp(accumulated.Z * scale + opaque.Blue * transmission, 0, 255));
				}
			});
		}
		rasterizationStopwatch.Stop();

		bitmapCopyStopwatch.Restart();
		cancellationToken.ThrowIfCancellationRequested();
		scene.Pixels = pixels;
		bitmapCopyStopwatch.Stop();

		double projectionMs = projectionStopwatch.Elapsed.TotalMilliseconds;
		double rasterizationMs = rasterizationStopwatch.Elapsed.TotalMilliseconds;
		double bitmapCopyMs = bitmapCopyStopwatch.Elapsed.TotalMilliseconds;
		string bottleneck = projectionMs >= rasterizationMs && projectionMs >= bitmapCopyMs
			? "preparation/projection"
			: rasterizationMs >= bitmapCopyMs ? "rasterization" : "bitmap copy";
		logger?.LogDebug(
			"ObjModelRenderer.Render {Width}x{Height}: preparation/projection {ProjectionMs:F3} ms | " +
			"rasterization {RasterizationMs:F3} ms | bitmap copy {BitmapCopyMs:F3} ms | " +
			"total {TotalMs:F3} ms | bottleneck: {Bottleneck} | parallel projection + raster rows, precomputed | " +
			"{Shading} shading",
			width, height, projectionMs, rasterizationMs, bitmapCopyMs,
			projectionMs + rasterizationMs + bitmapCopyMs, bottleneck, smoothShading ? "smooth" : "flat");
	}

	static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

	static Vertex ToCamera(Vertex vertex, Vector3 camera, Vector3 right, Vector3 up, Vector3 forward,
		Vector3 center, float radius)
	{
		Vector3 relative = (vertex.Position - center) / radius - camera;
		return new Vertex(new Vector3(Vector3.Dot(relative, right), Vector3.Dot(relative, up),
			Vector3.Dot(relative, forward)), vertex.Normal);
	}

	static int ClipNearPlane(ReadOnlySpan<Vertex> input, Span<Vertex> output)
	{
		const float NearPlane = 0.01f;
		int count = 0;
		Vertex previous = input[^1];
		foreach (Vertex current in input)
		{
			bool previousInside = previous.Position.Z >= NearPlane;
			bool currentInside = current.Position.Z >= NearPlane;
			if (previousInside != currentInside)
			{
				float fraction = (NearPlane - previous.Position.Z) / (current.Position.Z - previous.Position.Z);
				Vector3 position = Vector3.Lerp(previous.Position, current.Position, fraction);
				position.Z = NearPlane;
				output[count++] = new Vertex(position, Vector3.Lerp(previous.Normal, current.Normal, fraction));
			}

			if (currentInside)
			{
				output[count++] = current;
			}

			previous = current;
		}

		return count;
	}

	static ProjectedVertex Project(Vertex vertex, int width, int height, float focalLength, float normalSign)
	{
		float inverseDepth = 1 / vertex.Position.Z;
		return new ProjectedVertex(new Vector2(width / 2f + vertex.Position.X * focalLength * inverseDepth,
			height / 2f - vertex.Position.Y * focalLength * inverseDepth),
			inverseDepth, vertex.Normal * (inverseDepth * normalSign));
	}

	static ProjectedTriangle PrepareTriangle(ProjectedVertex a, ProjectedVertex b, ProjectedVertex c,
		int width, int height, SKColor baseColor, SKColor flatColor, float opacity)
	{
		Vector2 ab = b.Position - a.Position;
		Vector2 ac = c.Position - a.Position;
		float area = ab.X * ac.Y - ab.Y * ac.X;
		int minX = (int)Math.Clamp(MathF.Floor(Math.Min(a.Position.X, Math.Min(b.Position.X, c.Position.X))), 0, width);
		int maxX = (int)Math.Clamp(MathF.Ceiling(Math.Max(a.Position.X, Math.Max(b.Position.X, c.Position.X))), -1, width - 1);
		int minY = (int)Math.Clamp(MathF.Floor(Math.Min(a.Position.Y, Math.Min(b.Position.Y, c.Position.Y))), 0, height);
		int maxY = (int)Math.Clamp(MathF.Ceiling(Math.Max(a.Position.Y, Math.Max(b.Position.Y, c.Position.Y))), -1, height - 1);
		// Double precision keeps the reciprocal finite even for subnormal, nonzero float areas.
		double inverseArea = area == 0 ? 0 : 1.0 / area;
		return new ProjectedTriangle(a, b, c, area, inverseArea, minX, maxX, minY, maxY, baseColor, flatColor, opacity);
	}

	static bool IsTopLeft(Vector2 start, Vector2 end, float area)
	{
		Vector2 edge = area > 0 ? end - start : start - end;
		return edge.Y < 0 || (edge.Y == 0 && edge.X > 0);
	}

	void Rasterize(in ProjectedTriangle triangle, int width, bool smoothShading, ParallelOptions parallelOptions)
	{
		if (triangle.Area == 0 || triangle.MinX > triangle.MaxX || triangle.MinY > triangle.MaxY)
		{
			return;
		}

		ProjectedVertex a = triangle.A;
		ProjectedVertex b = triangle.B;
		ProjectedVertex c = triangle.C;
		double inverseArea = triangle.InverseArea;
		Vector3 normalA = a.NormalOverDepth;
		Vector3 normalB = b.NormalOverDepth;
		Vector3 normalC = c.NormalOverDepth;
		float lightX = LightDirection.X;
		float lightY = LightDirection.Y;
		float lightZ = LightDirection.Z;
		float red = triangle.BaseColor.Red;
		float green = triangle.BaseColor.Green;
		float blue = triangle.BaseColor.Blue;
		SKColor flatColor = triangle.FlatColor;
		float opacity = triangle.Opacity;
		bool transparent = opacity < 1;
		// A shared edge belongs to one face, so transparent seams are not blended twice.
		bool includeA = IsTopLeft(b.Position, c.Position, triangle.Area);
		bool includeB = IsTopLeft(c.Position, a.Position, triangle.Area);
		bool includeC = IsTopLeft(a.Position, b.Position, triangle.Area);
		int minX = triangle.MinX;
		int maxX = triangle.MaxX;
		int minY = triangle.MinY;
		int maxY = triangle.MaxY;
		CancellationToken cancellationToken = parallelOptions.CancellationToken;
		// Rows own disjoint pixels; completion before the next triangle preserves depth ordering.
		Parallel.For(minY, maxY + 1, parallelOptions, y =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			float pointY = y + 0.5f;
			float ay = a.Position.Y - pointY;
			float by = b.Position.Y - pointY;
			float cy = c.Position.Y - pointY;
			int rowIndex = y * width;
			for (int x = minX; x <= maxX; x++)
			{
				if ((x & 63) == 0)
				{
					cancellationToken.ThrowIfCancellationRequested();
				}

				float pointX = x + 0.5f;
				float ax = a.Position.X - pointX;
				float bx = b.Position.X - pointX;
				float cx = c.Position.X - pointX;
				float wa = (float)((bx * cy - by * cx) * inverseArea);
				float wb = (float)((cx * ay - cy * ax) * inverseArea);
				float wc = transparent ? (float)((ax * by - ay * bx) * inverseArea) : 1 - wa - wb;
				if (wa < 0 || wb < 0 || wc < 0 ||
					(transparent && ((wa == 0 && !includeA) || (wb == 0 && !includeB) || (wc == 0 && !includeC))))
				{
					continue;
				}

				float inverseDepth = wa * a.InverseDepth + wb * b.InverseDepth + wc * c.InverseDepth;
				int index = rowIndex + x;
				if (inverseDepth <= depthBuffer[index])
				{
					continue;
				}

				SKColor color = flatColor;
				if (smoothShading)
				{
					float normalX = wa * normalA.X + wb * normalB.X + wc * normalC.X;
					float normalY = wa * normalA.Y + wb * normalB.Y + wc * normalC.Y;
					float normalZ = wa * normalA.Z + wb * normalB.Z + wc * normalC.Z;
					float normalLengthSquared = normalX * normalX + normalY * normalY + normalZ * normalZ;
					float diffuse = 0;
					if (normalLengthSquared > 1e-12f)
					{
						float lightDotNormal = normalX * lightX + normalY * lightY + normalZ * lightZ;
						diffuse = Math.Max(0, lightDotNormal / MathF.Sqrt(normalLengthSquared));
					}
					float intensity = 0.25f + 0.75f * diffuse;
					color = new SKColor((byte)(red * intensity),
						(byte)(green * intensity), (byte)(blue * intensity));
				}

				if (transparent)
				{
					// Blend only fragments in front of opaque geometry, without writing transparent depth.
					float weight = opacity * Math.Clamp(inverseDepth * inverseDepth * inverseDepth, 0.01f, 3000f);
					transparentColors[index] += new Vector4(color.Red * weight, color.Green * weight, color.Blue * weight, weight);
					transmittance[index] *= 1 - opacity;
				}
				else
				{
					depthBuffer[index] = inverseDepth;
					pixels[index] = color;
				}
			}
		});
	}

	/// <summary>
	/// Renders the current image to the Skia surface, or clears the canvas to black when the surface is smaller than the
	/// minimum render size.
	/// </summary>
	/// <remarks>Uses linear filtering when scaling the bitmap to the target surface dimensions.</remarks>
	/// <param name="sender">The object that raised the paint event.</param>
	/// <param name="e">Provides surface, canvas, and size information for the paint operation.</param>
	public void PaintSurface(object? sender, SKPaintSurfaceEventArgs e)
	{
		if (e.Info.Width < 32 || e.Info.Height < 32)
		{
			Resize(e.Info.Width, e.Info.Height);
			e.Surface.Canvas.Clear(SKColors.Black);
			return;
		}

		SKBitmap bitmap = Resize(e.Info.Width, e.Info.Height);
		//e.Surface.Canvas.DrawBitmap(bitmap,
		//	new SKRect(0, 0, e.Info.Width, e.Info.Height),
		//	new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
		e.Surface.Canvas.DrawBitmap(bitmap,
			new SKRect(0, 0, e.Info.Width, e.Info.Height),
			new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
	}

	readonly record struct ProjectedVertex(Vector2 Position, float InverseDepth, Vector3 NormalOverDepth);
	readonly record struct ProjectedTriangle(ProjectedVertex A, ProjectedVertex B, ProjectedVertex C,
		float Area, double InverseArea, int MinX, int MaxX, int MinY, int MaxY, SKColor BaseColor, SKColor FlatColor,
		float Opacity);
}
