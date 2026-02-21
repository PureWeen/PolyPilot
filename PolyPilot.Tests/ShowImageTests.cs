using PolyPilot.Models;
using PolyPilot.Services;
using System.Text.Json;

namespace PolyPilot.Tests;

public class ShowImageTests
{
    [Fact]
    public void ImageMessage_SetsCorrectType()
    {
        var msg = ChatMessage.ImageMessage("/tmp/test.png", null, "A caption");
        Assert.Equal(ChatMessageType.Image, msg.MessageType);
        Assert.Equal("/tmp/test.png", msg.ImagePath);
        Assert.Equal("A caption", msg.Caption);
        Assert.True(msg.IsComplete);
        Assert.True(msg.IsSuccess);
        Assert.Equal("show_image", msg.ToolName);
    }

    [Fact]
    public void ImageMessage_WithDataUri()
    {
        var dataUri = "data:image/png;base64,iVBOR...";
        var msg = ChatMessage.ImageMessage(null, dataUri);
        Assert.Equal(ChatMessageType.Image, msg.MessageType);
        Assert.Null(msg.ImagePath);
        Assert.Equal(dataUri, msg.ImageDataUri);
        Assert.Null(msg.Caption);
    }

    [Fact]
    public void ParseResult_ValidJson()
    {
        var json = JsonSerializer.Serialize(new { displayed = true, persistent_path = "/home/user/.polypilot/images/abc.png", caption = "Screenshot" });
        var (path, caption) = ShowImageTool.ParseResult(json);
        Assert.Equal("/home/user/.polypilot/images/abc.png", path);
        Assert.Equal("Screenshot", caption);
    }

    [Fact]
    public void ParseResult_EmptyCaption_ReturnsNull()
    {
        var json = JsonSerializer.Serialize(new { displayed = true, persistent_path = "/tmp/img.png", caption = "" });
        var (path, caption) = ShowImageTool.ParseResult(json);
        Assert.Equal("/tmp/img.png", path);
        Assert.Null(caption);
    }

    [Fact]
    public void ParseResult_InvalidJson_ReturnsNulls()
    {
        var (path, caption) = ShowImageTool.ParseResult("not json");
        Assert.Null(path);
        Assert.Null(caption);
    }

    [Fact]
    public void ParseResult_Null_ReturnsNulls()
    {
        var (path, caption) = ShowImageTool.ParseResult(null);
        Assert.Null(path);
        Assert.Null(caption);
    }

    [Fact]
    public void ParseResult_ErrorResult_ReturnsNulls()
    {
        var json = JsonSerializer.Serialize(new { error = "File not found" });
        var (path, caption) = ShowImageTool.ParseResult(json);
        Assert.Null(path);
        Assert.Null(caption);
    }

    [Fact]
    public void ToolName_IsShowImage()
    {
        Assert.Equal("show_image", ShowImageTool.ToolName);
    }

    [Fact]
    public void CreateFunction_ReturnsNonNull()
    {
        var func = ShowImageTool.CreateFunction();
        Assert.NotNull(func);
        Assert.Contains("image", func.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolCompletedPayload_HasImageFields()
    {
        var payload = new ToolCompletedPayload
        {
            SessionName = "test",
            CallId = "c1",
            Result = "ok",
            Success = true,
            ImageData = "iVBOR...",
            ImageMimeType = "image/png",
            Caption = "Screenshot"
        };
        Assert.Equal("iVBOR...", payload.ImageData);
        Assert.Equal("image/png", payload.ImageMimeType);
        Assert.Equal("Screenshot", payload.Caption);
    }

    [Fact]
    public void ToolCompletedPayload_ImageFields_DefaultNull()
    {
        var payload = new ToolCompletedPayload { SessionName = "test", CallId = "c1", Result = "ok", Success = true };
        Assert.Null(payload.ImageData);
        Assert.Null(payload.ImageMimeType);
        Assert.Null(payload.Caption);
    }

    [Fact]
    public void ToolCompletedPayload_Serialization_IncludesImageFields()
    {
        var payload = new ToolCompletedPayload
        {
            SessionName = "s1",
            CallId = "c1",
            Result = "{}",
            Success = true,
            ImageData = "abc123",
            ImageMimeType = "image/jpeg",
            Caption = "My image"
        };
        var json = JsonSerializer.Serialize(payload);
        Assert.Contains("abc123", json);
        Assert.Contains("image/jpeg", json);
        Assert.Contains("My image", json);

        var deserialized = JsonSerializer.Deserialize<ToolCompletedPayload>(json);
        Assert.NotNull(deserialized);
        Assert.Equal("abc123", deserialized!.ImageData);
        Assert.Equal("image/jpeg", deserialized.ImageMimeType);
        Assert.Equal("My image", deserialized.Caption);
    }

    [Fact]
    public void ChatMessage_ImageType_InEnum()
    {
        // Verify Image is a valid enum value
        Assert.True(Enum.IsDefined(typeof(ChatMessageType), ChatMessageType.Image));
    }

    // --- FetchImage bridge protocol tests ---

    [Fact]
    public void FetchImagePayload_Serialization()
    {
        var payload = new FetchImagePayload { Path = "/tmp/screen.png", RequestId = "abc123" };
        var msg = BridgeMessage.Create(BridgeMessageTypes.FetchImage, payload);
        var json = msg.Serialize();
        var parsed = BridgeMessage.Deserialize(json);
        Assert.NotNull(parsed);
        Assert.Equal(BridgeMessageTypes.FetchImage, parsed!.Type);
        var p = parsed.GetPayload<FetchImagePayload>();
        Assert.NotNull(p);
        Assert.Equal("/tmp/screen.png", p!.Path);
        Assert.Equal("abc123", p.RequestId);
    }

    [Fact]
    public void FetchImageResponsePayload_WithData()
    {
        var payload = new FetchImageResponsePayload
        {
            RequestId = "abc123",
            ImageData = "iVBOR...",
            MimeType = "image/png"
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.FetchImageResponse, payload);
        var json = msg.Serialize();
        var parsed = BridgeMessage.Deserialize(json);
        var p = parsed!.GetPayload<FetchImageResponsePayload>();
        Assert.NotNull(p);
        Assert.Equal("abc123", p!.RequestId);
        Assert.Equal("iVBOR...", p.ImageData);
        Assert.Equal("image/png", p.MimeType);
        Assert.Null(p.Error);
    }

    [Fact]
    public void FetchImageResponsePayload_WithError()
    {
        var payload = new FetchImageResponsePayload
        {
            RequestId = "abc123",
            Error = "File not found"
        };
        var msg = BridgeMessage.Create(BridgeMessageTypes.FetchImageResponse, payload);
        var json = msg.Serialize();
        var parsed = BridgeMessage.Deserialize(json);
        var p = parsed!.GetPayload<FetchImageResponsePayload>();
        Assert.NotNull(p);
        Assert.Equal("File not found", p!.Error);
        Assert.Null(p.ImageData);
    }

    [Fact]
    public void BridgeMessageTypes_HasFetchImageConstants()
    {
        Assert.Equal("fetch_image", BridgeMessageTypes.FetchImage);
        Assert.Equal("fetch_image_response", BridgeMessageTypes.FetchImageResponse);
    }
}
