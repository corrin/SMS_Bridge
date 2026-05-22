namespace SMS_Bridge.Services;

internal static class AppData
{
    public static readonly string BasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SMS_Bridge"
    );
}
