namespace PolyPilot.Models;

/// <summary>
/// Translates raw platform-specific exception messages into user-friendly text.
/// Apple platforms (Mac Catalyst / iOS) embed WebExceptionStatus enum values
/// like "net_webstatus_ConnectFailure" in HttpRequestException/WebSocketException messages.
/// </summary>
public static class ErrorMessageHelper
{
    /// <summary>
    /// Returns a human-readable error message by unwrapping inner exceptions and
    /// replacing known platform-specific status codes.
    /// </summary>
    public static string Humanize(Exception ex)
    {
        // Walk the exception chain to find the most specific message
        var message = GetBestMessage(ex);
        return HumanizeMessage(message);
    }

    /// <summary>
    /// Replaces known platform-specific error patterns in a raw message string.
    /// </summary>
    public static string HumanizeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "An unknown error occurred";

        // Apple platform NSUrlSession error codes (WebExceptionStatus enum names)
        if (message.Contains("net_webstatus_ConnectFailure", StringComparison.OrdinalIgnoreCase))
            return "Connection refused — the server is not reachable. Check the address and make sure the server is running.";

        if (message.Contains("net_webstatus_NameResolutionFailure", StringComparison.OrdinalIgnoreCase))
            return "Could not resolve the server address. Check the URL and your network connection.";

        if (message.Contains("net_webstatus_Timeout", StringComparison.OrdinalIgnoreCase))
            return "Connection timed out. The server may be unreachable or too slow to respond.";

        if (message.Contains("net_webstatus_SecureChannelFailure", StringComparison.OrdinalIgnoreCase))
            return "SSL/TLS connection failed. The server may not support secure connections on this address.";

        if (message.Contains("net_webstatus_TrustFailure", StringComparison.OrdinalIgnoreCase))
            return "The server's SSL certificate is not trusted. Check the server address.";

        if (message.Contains("net_webstatus_ReceiveFailure", StringComparison.OrdinalIgnoreCase))
            return "Connection was lost while receiving data. The server may have closed the connection.";

        if (message.Contains("net_webstatus_SendFailure", StringComparison.OrdinalIgnoreCase))
            return "Failed to send data to the server. The connection may have been interrupted.";

        // Generic socket errors that may appear on any platform
        if (message.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
            return "Connection refused — the server is not reachable. Check the address and make sure the server is running.";

        if (message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase)
            || message.Contains("nodename nor servname provided", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase))
            return "Could not resolve the server address. Check the URL and your network connection.";

        if (message.Contains("Network is unreachable", StringComparison.OrdinalIgnoreCase))
            return "Network is unreachable. Check your internet connection.";

        if (message.Contains("No route to host", StringComparison.OrdinalIgnoreCase))
            return "Cannot reach the server. Check the address and your network connection.";

        if (message.Contains("Host is down", StringComparison.OrdinalIgnoreCase))
            return "The server appears to be down. Try again later.";

        // Catch-all for any other net_webstatus_ codes we haven't mapped
        if (message.Contains("net_webstatus_", StringComparison.OrdinalIgnoreCase))
            return "A network error occurred. Check your connection and try again.";

        return message;
    }

    private static string GetBestMessage(Exception ex)
    {
        // Prefer the innermost exception's message for SocketException etc.
        var inner = ex;
        while (inner.InnerException != null)
            inner = inner.InnerException;

        // If the inner message is more specific, use it; otherwise use the outer
        var innerMsg = inner.Message;
        var outerMsg = ex.Message;

        // If outer contains a platform status code, use that (it has the full context)
        if (outerMsg.Contains("net_webstatus_", StringComparison.OrdinalIgnoreCase))
            return outerMsg;

        // If inner is more specific (e.g. SocketException "Connection refused"), prefer it
        if (inner != ex && !string.IsNullOrWhiteSpace(innerMsg))
            return innerMsg;

        return outerMsg;
    }
}
