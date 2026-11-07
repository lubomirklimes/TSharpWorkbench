using System.Buffers.Binary;
using TSharpWorkbench.Testing;
using Xunit;

namespace TSharpWorkbench.Tests.Core;

public sealed class TestingDiagnosticsTests
{
    [Fact]
    public void SnapshotOwnsItsCellsAndRoundTripsAttributes()
    {
        var source = new[] {new TScreenChar('č', 0x9e), new TScreenChar('╔',0x27)};
        var snapshot = new ScreenSnapshot(2,1,source);
        source[0] = new('X',0);
        var restored = ScreenSnapshot.Deserialize(snapshot.Serialize());
        Assert.Equal('č',restored[0,0].Character);
        Assert.Equal((byte)0x9e,(byte)restored[0,0].Attr);
        var cell = restored[0,0]; cell.Character = 'Z';
        Assert.Equal('č', restored[0,0].Character);
        Assert.Throws<ArgumentOutOfRangeException>(() => restored[2,0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => restored[0,-1]);
    }

    [Theory]
    [InlineData(0,1,0)] [InlineData(201,1,201)] [InlineData(1,101,101)] [InlineData(2,2,3)]
    public void RejectsInvalidSnapshotGeometry(int width, int height, int count) =>
        Assert.Throws<ArgumentException>(() => new ScreenSnapshot(width,height,new TScreenChar[count]));

    [Fact]
    public void RejectsMalformedSnapshot()
    {
        Assert.Throws<InvalidDataException>(() => ScreenSnapshot.Deserialize([]));
        byte[] bytes = new ScreenSnapshot(1,1,[new('A',7)]).Serialize();
        Assert.Throws<InvalidDataException>(() => ScreenSnapshot.Deserialize(bytes[..^1]));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4),int.MaxValue);
        Assert.Throws<InvalidDataException>(() => ScreenSnapshot.Deserialize(bytes));
    }

    [Fact]
    public void PngIsDeterministicAndUsesPackedBackgroundNibble()
    {
        var snapshot = new ScreenSnapshot(1,1,[new(' ',0x9e)]);
        byte[] png = LogicalPng.Encode(snapshot);
        Assert.Equal(png, LogicalPng.Encode(ScreenSnapshot.Deserialize(snapshot.Serialize())));
        Assert.Equal(new byte[] {137,80,78,71,13,10,26,10},png[..8]);
        Assert.Equal(8,BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(12,BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.Equal(0x5555ffu,LogicalPng.Color(snapshot[0,0].Attr.Background >> 4));
        Assert.Equal(0xffff55u,LogicalPng.Color(snapshot[0,0].Attr.Foreground));
        // IHDR occupies bytes 8..32, IDAT header 33..40.
        int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(33));
        using var compressed = new MemoryStream(png,41,length);
        using var zip = new System.IO.Compression.ZLibStream(compressed,System.IO.Compression.CompressionMode.Decompress);
        var row = new byte[25]; zip.ReadExactly(row);
        Assert.Equal(0,row[0]); Assert.Equal(new byte[] {0x55,0x55,0xff},row[1..4]);
    }

    [Fact]
    public void GlyphsSupportBoxesCzechMarksBlocksAndVisibleFallback()
    {
        Assert.True(LogicalPng.Glyph('╔',7,4));
        Assert.False(LogicalPng.Glyph('╔',0,0));
        Assert.True(LogicalPng.Glyph('č',2,0));
        Assert.False(LogicalPng.Glyph('c',2,0));
        Assert.True(LogicalPng.Glyph('▀',0,0));
        Assert.False(LogicalPng.Glyph('▀',0,8));
        Assert.True(LogicalPng.Glyph('\ud800',1,2));
        foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.,:;!?-_=+*/\\()[]{}<>|\"'`~@#$%^&")
            Assert.Contains(true, Enumerable.Range(0,96).Select(i => LogicalPng.Glyph(c,i%8,i/8)));
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(Protocol.MaximumBytes + 1)] [InlineData(int.MaxValue)]
    public async Task RejectsInvalidFrameBeforeAllocation(int length)
    {
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header,length);
        await Assert.ThrowsAsync<InvalidDataException>(() => Protocol.ReadAsync<Request>(new MemoryStream(header),default));
    }

    [Fact]
    public async Task RejectsTruncatedMalformedAndOversizedFrames()
    {
        await Assert.ThrowsAsync<EndOfStreamException>(() => Protocol.ReadAsync<Request>(new MemoryStream([1,0,0]),default));
        await Assert.ThrowsAsync<EndOfStreamException>(() => Protocol.ReadAsync<Request>(new MemoryStream([5,0,0,0,1]),default));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => Protocol.ReadAsync<Request>(new MemoryStream([1,0,0,0,255]),default));
        await Assert.ThrowsAsync<InvalidDataException>(() => Protocol.WriteAsync(new MemoryStream(),new string('x',Protocol.MaximumBytes),default));
    }

    [Fact]
    public async Task FramesRoundTripAndCancellationIsBounded()
    {
        var request = new Request(1,Protocol.Schema,"instance","request","secret","Handshake","sample/x","menu-open",new InputStep("key","kbAltF"));
        using var stream = new MemoryStream(); await Protocol.WriteAsync(stream,request,default); stream.Position = 0;
        Assert.Equal(request,await Protocol.ReadAsync<Request>(stream,default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Protocol.ReadAsync<Request>(stream,cancelled.Token));
    }

    [Fact]
    public void AuthenticationRejectsVersionInstanceSecretAndNullFields()
    {
        var request = new Request(1,Protocol.Schema,"instance","request","secret","Handshake");
        Assert.True(Protocol.Authenticate(request,"instance","secret"));
        Assert.True(Protocol.Authenticate(request with {Checkpoint="menu-open",Input=new InputStep("action","open-note")},"instance","secret"));
        foreach (var invalid in new[] {request with {Version=2},request with {Instance="other"},request with {Secret="wrong!"},
            request with {Id=""},request with {Secret=null!},request with {Id=null!},request with {Operation=null!},
            request with {Schema=null!},request with {Schema="tsharpvision.testing/v1"},request with {Checkpoint="Bad--Name"},
            request with {Input=new InputStep("mouse","kbEsc")},request with {Input=new InputStep("key","")}})
            Assert.False(Protocol.Authenticate(invalid,"instance","secret"));
    }
}
