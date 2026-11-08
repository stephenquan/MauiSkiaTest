// MainActivity.cs

using Android.App;
using Android.Content.PM;

namespace MauiSkiaTest;

/// <summary>
/// Represents the Android entry activity for the .NET MAUI application.
/// </summary>
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
