namespace AppUpdateServer.Data.Entities;

public class AppVersion
{
    public int Id { get; set; }
    public int AppId { get; set; }
    public App App { get; set; } = null!;
    public string VersionString { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Sha256Hash { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public long FileSizeBytes { get; set; }
}
