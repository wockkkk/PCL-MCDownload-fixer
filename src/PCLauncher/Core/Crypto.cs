using System;
using System.IO;
using System.Security.Cryptography;

namespace PCLauncher.Core
{
    public static class Crypto
    {
        public static string Sha256File(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    var sb = new System.Text.StringBuilder(hash.Length * 2);
                    foreach (byte b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[hash] 计算失败：" + path + " -> " + ex.Message);
                return null;   // 失败必须返回 null，避免两次失败被当成“未修改”
            }
        }
    }
}
