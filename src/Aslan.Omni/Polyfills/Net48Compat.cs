#if NET48 || NETFRAMEWORK
using System.Security.Cryptography;
using System.Text;

namespace Aslan.Omni.Polyfills
{
    internal static class Net48Compat
    {
        public static string ToHexString(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static byte[] Sha256Hash(byte[] data)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(data);
        }

        public static byte[] GetRandomBytes(int count)
        {
            var buf = new byte[count];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(buf);
            return buf;
        }
    }
}
#endif
