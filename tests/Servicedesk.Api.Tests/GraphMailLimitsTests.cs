using Servicedesk.Infrastructure.Mail.Graph;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Guards the v0.0.77 large-attachment constants: the base64-vs-uploadSession
/// threshold, the Graph chunking contract (multiples of 320 KiB, under 4 MB
/// per PUT), and consistency between the registered settings default and the
/// in-code fallback.
public sealed class GraphMailLimitsTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(3 * 1024 * 1024, false)]      // exactly at the limit → simple POST
    [InlineData(3 * 1024 * 1024 + 1, true)]   // one byte over → upload session
    [InlineData(150L * 1024 * 1024, true)]
    public void RequiresUploadSession_splits_on_the_simple_attachment_limit(long size, bool expected)
        => Assert.Equal(expected, GraphMailLimits.RequiresUploadSession(size));

    [Fact]
    public void Upload_chunk_size_is_a_320KiB_multiple_under_4MB()
    {
        Assert.True(GraphMailLimits.UploadChunkBytes > 0);
        Assert.Equal(0, GraphMailLimits.UploadChunkBytes % (320 * 1024));
        Assert.True(GraphMailLimits.UploadChunkBytes < 4 * 1024 * 1024);
    }

    [Fact]
    public void Registered_cap_default_is_25MB_and_within_upload_limits()
    {
        var def = SettingDefaults.All.Single(d => d.Key == SettingKeys.Mail.MaxOutboundTotalBytes);
        var bytes = long.Parse(def.Value);
        Assert.Equal(25L * 1024 * 1024, bytes);
        // The cap exists to keep the total inside what a single mail can
        // carry; it must stay above the simple-POST threshold or the upload
        // session path would be unreachable by default.
        Assert.True(bytes > GraphMailLimits.SimpleAttachmentMaxBytes);
    }
}
