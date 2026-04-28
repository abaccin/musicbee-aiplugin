using System;
using System.Security.Cryptography;
using System.Text;

public static class FingerprintHelper
{
    public static string ComputeFingerprint(DbTrackRow m)
    {
        var s = string.Join("|", m.Artist ?? "", m.Title ?? "", m.Album ?? "", m.Genre ?? "", m.Year ?? "", m.Comment ?? "");
        using (var sha = SHA256.Create())
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            var hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "");
        }
    }
}