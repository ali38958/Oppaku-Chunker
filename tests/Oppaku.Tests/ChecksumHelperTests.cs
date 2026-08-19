using System.Security.Cryptography;
using System.IO;
using Oppaku.Core.Helpers;

namespace Oppaku.Tests;

public class ChecksumHelperTests
{
    [Fact]
    public void ComputeFileHash_And_ComputeSpanChecksum_Match()
    {
        var testFilePath = Path.GetTempFileName();
        try
        {
            // 1 KB of random data
            var data = new byte[1024];
            Random.Shared.NextBytes(data);
            File.WriteAllBytes(testFilePath, data);

            var expectedHashBytes = SHA256.HashData(data);
            var expectedHex = $"sha256:{Convert.ToHexString(expectedHashBytes).ToLowerInvariant()}";

            var fileHash = ChecksumHelper.ComputeFileHash(testFilePath);
            var spanHash = ChecksumHelper.ComputeSpanChecksum(data);

            Assert.Equal(expectedHex, fileHash);
            Assert.Equal(expectedHex, spanHash);
        }
        finally
        {
            if (File.Exists(testFilePath))
            {
                File.Delete(testFilePath);
            }
        }
    }
}
