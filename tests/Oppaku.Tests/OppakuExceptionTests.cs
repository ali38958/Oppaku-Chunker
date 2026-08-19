using Xunit;
using Oppaku.Core.Exceptions;

namespace Oppaku.Tests;

public class OppakuExceptionTests
{
    [Fact]
    public void OppakuException_CanBeThrownAndCaught()
    {
        var code = ErrorCode.ChecksumMismatch;
        var message = "Checksums do not match.";

        var exception = Assert.Throws<OppakuException>((Action)(() =>
        {
            throw new OppakuException(code, message);
        }));

        Assert.Equal(code, exception.Code);
        Assert.Equal(message, exception.Message);
    }
}
