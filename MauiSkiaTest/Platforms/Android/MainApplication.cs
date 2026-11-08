// MainApplication.cs

using Android.App;
using Android.Runtime;

namespace MauiSkiaTest;

/// <summary>
/// Represents the Android application class for the .NET MAUI app and supplies the MAUI application instance at
/// startup.
/// </summary>
[Application]
public class MainApplication : MauiApplication
{
	/// <summary>
	/// Initializes a new instance of the <see cref="MainApplication"/> class from an Android runtime handle.
	/// </summary>
	/// <param name="handle">The native Android object handle.</param>
	/// <param name="ownership">The ownership of the native handle.</param>
	public MainApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership)
	{
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
