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
}
