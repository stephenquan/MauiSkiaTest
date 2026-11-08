using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;

namespace MauiSkiaTest;

/// <summary>
/// An immutable, in-memory Wavefront OBJ polygon mesh.
/// </summary>
/// <remarks>
/// Supports vertex positions, normals, and simple planar polygon faces, including negative indices.
/// Supports per-face RGB diffuse colors and opacity from MTL libraries.
/// Texture coordinates are accepted but textures, other material properties, lines, and free-form surfaces are not stored.
/// </remarks>
public sealed class ObjModel
{
	readonly Triangle[] triangles;
	static ILogger<ObjModel>? Logger => IPlatformApplication.Current?.Services.GetService<ILogger<ObjModel>>();

	ObjModel(List<Vector3> positions, int normalCount, List<Triangle> triangles, CancellationToken cancellationToken)
	{
		VertexCount = positions.Count;
		NormalCount = normalCount;
		this.triangles = triangles.ToArray();
		HasTransparency = triangles.Any(triangle => triangle.Opacity < 1);
		Vector3 minimum = positions[0];
		Vector3 maximum = minimum;
		foreach (Vector3 position in positions)
		{
			cancellationToken.ThrowIfCancellationRequested();
			minimum = Vector3.Min(minimum, position);
			maximum = Vector3.Max(maximum, position);
		}

		Center = minimum / 2 + maximum / 2;
		foreach (Vector3 position in positions)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Radius = Math.Max(Radius, Vector3.Distance(Center, position));
		}

		if (!float.IsFinite(Radius) || Radius <= 0)
		{
			throw new FormatException("The OBJ must have finite, nonzero spatial bounds.");
		}
	}

	/// <summary>The number of source vertex positions.</summary>
	public int VertexCount { get; }

	/// <summary>The number of source vertex normals.</summary>
	public int NormalCount { get; }

	/// <summary>The number of triangles after polygon triangulation.</summary>
	public int TriangleCount => triangles.Length;

	/// <summary>The center of the mesh's axis-aligned bounds, in OBJ coordinates.</summary>
	public Vector3 Center { get; }

	/// <summary>A bounding-sphere radius around <see cref="Center"/>, in OBJ units.</summary>
	public float Radius { get; }

	internal ReadOnlySpan<Triangle> Triangles => triangles;
	internal bool HasTransparency { get; }

	/// <summary>Parses OBJ text and optional MTL text keyed by the filenames in its mtllib declarations.
	/// Malformed geometry, RGB diffuse colors, or opacity values throw <see cref="FormatException"/>.</summary>
	/// <remarks>Without supplied material libraries, faces have no diffuse color and use the renderer's default color.</remarks>
	/// <param name="text">The OBJ geometry.</param>
	/// <param name="materialLibraries">Optional MTL text keyed by filename.</param>
	/// <param name="cancellationToken">Cancels parsing and mesh construction. Defaults to no cancellation.</param>
	public static ObjModel LoadFromText(string text, IReadOnlyDictionary<string, string>? materialLibraries = null,
		CancellationToken cancellationToken = default)
	{
		return CreateModel(ParseObj(text, cancellationToken), materialLibraries, cancellationToken);
	}

	static ParsedObj ParseObj(string text, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(text);
		cancellationToken.ThrowIfCancellationRequested();
		List<Vector3> positions = [];
		List<Vector3> normals = [];
		List<Triangle> triangles = [];
		List<string> materialLibraryNames = [];
		string? materialName = null;
		int textureCount = 0;
		int lineNumber = 0;
		using var reader = new StringReader(text.TrimStart('\uFEFF'));
		while (reader.ReadLine() is string line)
		{
			cancellationToken.ThrowIfCancellationRequested();
			lineNumber++;
			string[] parts = ReadParts(line);
			if (parts.Length == 0)
			{
				continue;
			}

			try
			{
				switch (parts[0])
				{
					case "mtllib":
						if (parts.Length < 2)
						{
							throw new FormatException("A material library requires a filename.");
						}

						materialLibraryNames.AddRange(parts[1..]);
						break;
					case "usemtl":
						if (parts.Length < 2)
						{
							throw new FormatException("A material assignment requires a name.");
						}

						materialName = string.Join(" ", parts[1..]);
						break;
					case "v":
						if (parts.Length is not (4 or 5))
						{
							throw new FormatException("A vertex requires three coordinates and an optional weight.");
						}

						Vector3 position = ReadVector(parts);
						if (parts.Length == 5)
						{
							float weight = ReadNumber(parts[4]);
							if (weight == 0)
							{
								throw new FormatException("A vertex weight cannot be zero.");
							}

							position /= weight;
						}

						if (!IsFinite(position))
						{
							throw new FormatException("Vertex coordinates must be finite.");
						}

						positions.Add(position);
						break;
					case "vn":
						if (parts.Length != 4)
						{
							throw new FormatException("A normal requires three coordinates.");
						}

						Vector3 normal = ReadVector(parts);
						if (!float.IsFinite(normal.LengthSquared()) || normal.LengthSquared() <= 0)
						{
							throw new FormatException("A normal must have finite, nonzero length.");
						}

						normals.Add(Vector3.Normalize(normal));
						break;
					case "vt":
						if (parts.Length is < 2 or > 4)
						{
							throw new FormatException("A texture coordinate requires one to three coordinates.");
						}

						foreach (string value in parts[1..])
						{
							ReadNumber(value);
						}

						textureCount++;
						break;
					case "f":
						if (parts.Length < 4)
						{
							throw new FormatException("A face requires at least three vertices.");
						}

						List<Vertex> polygon = [];
						foreach (string value in parts[1..])
						{
							cancellationToken.ThrowIfCancellationRequested();
							string[] indices = value.Split('/');
							if (indices.Length is < 1 or > 3 ||
								(indices.Length == 2 && indices[1].Length == 0) ||
								(indices.Length == 3 && indices[2].Length == 0))
							{
								throw new FormatException("Expected v, v/vt, v//vn, or v/vt/vn face indices.");
							}

							Vector3 point = positions[ReadIndex(indices[0], positions.Count)];
							if (indices.Length >= 2 && indices[1].Length > 0)
							{
								ReadIndex(indices[1], textureCount);
							}

							Vector3 vertexNormal = indices.Length == 3
								? normals[ReadIndex(indices[2], normals.Count)]
								: Vector3.Zero;
							polygon.Add(new Vertex(point, vertexNormal));
						}

						Triangulate(polygon, triangles, materialName, cancellationToken);
						break;
				}
			}
			catch (FormatException exception)
			{
				throw new FormatException($"OBJ line {lineNumber}: {exception.Message}", exception);
			}
		}

		if (triangles.Count == 0)
		{
			throw new FormatException("The OBJ contains no polygon faces.");
		}

		return new ParsedObj(positions, normals.Count, triangles, materialLibraryNames);
	}

	/// <summary>Reads UTF-8 OBJ geometry asynchronously, leaving the caller's stream open.</summary>
	/// <remarks>A stream has no library directory; use <see cref="LoadFromText"/> to supply MTL text,
	/// or <see cref="LoadFromMauiAsset"/> to resolve packaged libraries automatically.</remarks>
	public static async Task<ObjModel> LoadFromStream(Stream stream, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(stream);
		return LoadFromText(await ReadText(stream, cancellationToken).ConfigureAwait(false),
			cancellationToken: cancellationToken);
	}

	/// <summary>Loads a packaged OBJ mesh and its referenced MTL libraries, relative to the OBJ filename.</summary>
	/// <remarks>Missing libraries are logged and leave their faces without diffuse colors.
	/// Malformed libraries and other I/O failures are not ignored.</remarks>
	public static async Task<ObjModel> LoadFromMauiAsset(string filename, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(filename);
		cancellationToken.ThrowIfCancellationRequested();
		using var stream = await FileSystem.OpenAppPackageFileAsync(filename).ConfigureAwait(false);
		ParsedObj parsed = ParseObj(await ReadText(stream, cancellationToken).ConfigureAwait(false), cancellationToken);
		Dictionary<string, string> libraries = new(StringComparer.Ordinal);
		string normalizedFilename = filename.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
		string directory = Path.GetDirectoryName(normalizedFilename) ?? string.Empty;
		foreach (string libraryName in parsed.MaterialLibraryNames.Distinct(StringComparer.Ordinal))
		{
			cancellationToken.ThrowIfCancellationRequested();
			string normalizedLibraryName = libraryName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
			string libraryFilename = Path.Combine(directory, normalizedLibraryName);
			Stream libraryStream;
			try
			{
				libraryStream = await FileSystem.OpenAppPackageFileAsync(libraryFilename).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
			{
				Logger?.LogWarning(exception, "Material library {LibraryFilename} referenced by {ObjFilename} was not found. Using default face colors.",
					libraryFilename, filename);
				continue;
			}

			using (libraryStream)
			{
				libraries.Add(libraryName, await ReadText(libraryStream, cancellationToken).ConfigureAwait(false));
			}
		}

		cancellationToken.ThrowIfCancellationRequested();
		return CreateModel(parsed, libraries, cancellationToken);
	}

	static async Task<string> ReadText(Stream stream, CancellationToken cancellationToken)
	{
		using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
		string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		return text;
	}

	static ObjModel CreateModel(ParsedObj parsed, IReadOnlyDictionary<string, string>? materialLibraries,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Dictionary<string, Material> materials = new(StringComparer.Ordinal);
		foreach (string libraryName in parsed.MaterialLibraryNames.Distinct(StringComparer.Ordinal))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (materialLibraries is not null && materialLibraries.TryGetValue(libraryName, out string? text))
			{
				ArgumentNullException.ThrowIfNull(text);
				ReadMaterials(text, libraryName, materials, cancellationToken);
			}
		}

		HashSet<string> missingMaterials = new(StringComparer.Ordinal);
		for (int i = 0; i < parsed.Triangles.Count; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Triangle triangle = parsed.Triangles[i];
			if (triangle.MaterialName is string materialName)
			{
				if (materials.TryGetValue(materialName, out Material material))
				{
					parsed.Triangles[i] = triangle with { DiffuseColor = material.DiffuseColor, Opacity = material.Opacity };
				}
				else if (materials.Count > 0 && missingMaterials.Add(materialName))
				{
					Logger?.LogWarning("OBJ material {MaterialName} is not defined in the supplied libraries. Using the default face color.", materialName);
				}
			}
		}

		return new ObjModel(parsed.Positions, parsed.NormalCount, parsed.Triangles, cancellationToken);
	}

	static void ReadMaterials(string text, string filename, Dictionary<string, Material> materials,
		CancellationToken cancellationToken)
	{
		using var reader = new StringReader(text.TrimStart('\uFEFF'));
		string? materialName = null;
		int lineNumber = 0;
		while (reader.ReadLine() is string line)
		{
			cancellationToken.ThrowIfCancellationRequested();
			lineNumber++;
			string[] parts = ReadParts(line);
			if (parts.Length == 0)
			{
				continue;
			}

			try
			{
				switch (parts[0])
				{
					case "newmtl":
						if (parts.Length < 2)
						{
							throw new FormatException("A material requires a name.");
						}

						materialName = string.Join(" ", parts[1..]);
						materials[materialName] = new Material(null, 1);
						break;
					case "Kd":
						if (materialName is null || parts.Length != 4)
						{
							throw new FormatException("An RGB diffuse color requires a material and three components.");
						}

						Vector3 color = ReadVector(parts);
						if (color.X < 0 || color.X > 1 || color.Y < 0 || color.Y > 1 || color.Z < 0 || color.Z > 1)
						{
							throw new FormatException("Diffuse color components must be between zero and one.");
						}

						materials[materialName] = materials[materialName] with { DiffuseColor = color };
						break;
					case "d":
					case "Tr":
						if (materialName is null || parts.Length != 2)
						{
							throw new FormatException("Opacity requires a material and one component.");
						}

						float opacity = ReadNumber(parts[1]);
						if (opacity < 0 || opacity > 1)
						{
							throw new FormatException("Opacity must be between zero and one.");
						}

						materials[materialName] = materials[materialName] with
						{
							Opacity = parts[0] == "Tr" ? 1 - opacity : opacity
						};
						break;
				}
			}
			catch (FormatException exception)
			{
				throw new FormatException($"MTL '{filename}' line {lineNumber}: {exception.Message}", exception);
			}
		}
	}

	static string[] ReadParts(string line)
	{
		int comment = line.IndexOf('#');
		if (comment >= 0)
		{
			line = line[..comment];
		}

		return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
	}

	static float ReadNumber(string value)
	{
		if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) ||
			!float.IsFinite(result))
		{
			throw new FormatException($"Invalid finite number '{value}'.");
		}

		return result;
	}

	static Vector3 ReadVector(string[] parts) => new(ReadNumber(parts[1]), ReadNumber(parts[2]), ReadNumber(parts[3]));

	static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

	static int ReadIndex(string value, int count)
	{
		if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int index) ||
			index == 0 || index > count || index < -count)
		{
			throw new FormatException($"Index '{value}' is outside the {count} available entries.");
		}

		return index > 0 ? index - 1 : count + index;
	}

	static void Triangulate(List<Vertex> polygon, List<Triangle> triangles, string? materialName,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		// Normalize locally to keep the triangulation tolerance independent of OBJ units.
		Vector3 origin = polygon[0].Position;
		float scale = polygon.Max(vertex => Vector3.Distance(origin, vertex.Position));
		if (!float.IsFinite(scale) || scale <= 0)
		{
			throw new FormatException("A face must have finite, nonzero area.");
		}

		Vector3[] points = polygon.Select(vertex => (vertex.Position - origin) / scale).ToArray();
		Vector3 normal = Vector3.Zero;
		for (int i = 1; i < points.Length - 1; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			normal += Vector3.Cross(points[i], points[i + 1]);
		}

		if (normal.LengthSquared() < 1e-12f)
		{
			throw new FormatException("A face must have nonzero area and consistent winding.");
		}

		normal = Vector3.Normalize(normal);
		if (points.Any(point => Math.Abs(Vector3.Dot(point, normal)) > 1e-4f))
		{
			throw new FormatException("Polygon faces must be planar.");
		}

		Vector3 absolute = Vector3.Abs(normal);
		Vector2[] projected = points.Select(point => absolute.X >= absolute.Y && absolute.X >= absolute.Z
			? new Vector2(point.Y, point.Z)
			: absolute.Y >= absolute.Z ? new Vector2(point.Z, point.X) : new Vector2(point.X, point.Y)).ToArray();
		float area = 0;
		for (int i = 0; i < projected.Length; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			area += Cross(projected[i], projected[(i + 1) % projected.Length]);
			Vertex vertex = polygon[i];
			if (vertex.Normal == Vector3.Zero)
			{
				polygon[i] = vertex with { Normal = normal };
			}
		}

		float sign = MathF.Sign(area);
		List<int> remaining = Enumerable.Range(0, polygon.Count).ToList();
		while (remaining.Count > 3)
		{
			cancellationToken.ThrowIfCancellationRequested();
			bool removed = false;
			for (int i = 0; i < remaining.Count; i++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				int a = remaining[(i + remaining.Count - 1) % remaining.Count];
				int b = remaining[i];
				int c = remaining[(i + 1) % remaining.Count];
				float turn = Cross(projected[b] - projected[a], projected[c] - projected[a]) * sign;
				if (Math.Abs(turn) <= 1e-7f)
				{
					remaining.RemoveAt(i);
					removed = true;
					break;
				}

				if (turn < 0 || remaining.Any(index =>
				{
					cancellationToken.ThrowIfCancellationRequested();
					return index != a && index != b && index != c &&
						InsideTriangle(projected[index], projected[a], projected[b], projected[c], sign);
				}))
				{
					continue;
				}

				triangles.Add(new Triangle(polygon[a], polygon[b], polygon[c], normal, materialName, null));
				remaining.RemoveAt(i);
				removed = true;
				break;
			}

			if (!removed)
			{
				throw new FormatException("Cannot triangulate the face; use a simple, non-self-intersecting polygon.");
			}
		}

		if (Cross(projected[remaining[1]] - projected[remaining[0]],
			projected[remaining[2]] - projected[remaining[0]]) * sign <= 1e-7f)
		{
			throw new FormatException("The face contains a degenerate triangle.");
		}

		triangles.Add(new Triangle(polygon[remaining[0]], polygon[remaining[1]], polygon[remaining[2]], normal, materialName, null));
	}

	static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

	static bool InsideTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c, float sign) =>
		Cross(b - a, point - a) * sign >= -1e-7f &&
		Cross(c - b, point - b) * sign >= -1e-7f &&
		Cross(a - c, point - c) * sign >= -1e-7f;

	internal readonly record struct Vertex(Vector3 Position, Vector3 Normal);
	internal readonly record struct Triangle(Vertex A, Vertex B, Vertex C, Vector3 FaceNormal,
		string? MaterialName, Vector3? DiffuseColor, float Opacity = 1);
	readonly record struct Material(Vector3? DiffuseColor, float Opacity);
	sealed record ParsedObj(List<Vector3> Positions, int NormalCount, List<Triangle> Triangles,
		List<string> MaterialLibraryNames);
}
