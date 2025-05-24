using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Collections.Generic; // Needed for Dictionary

public class SimpleWebServer
{
    private readonly int port;
    private readonly string webRootPath;
    private TcpListener listener;
    private bool isRunning = false;

    // Allowed file extensions and their MIME types
    private Dictionary<string, string> mimeTypes = new Dictionary<string, string>()
    {
        { ".html", "text/html" },
        { ".css", "text/css" },
        { ".js", "application/javascript" }, // Standard MIME type for JS
        // Add more if needed, but stick to requirements for now
    };

    public SimpleWebServer(int port, string webRootPath)
    {
        this.port = port;
        // Ensure the web root path is an absolute path and ends with a separator
        this.webRootPath = Path.GetFullPath(webRootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        listener = new TcpListener(IPAddress.Any, port);
    }

    public void Start()
    {
        try
        {
            listener.Start();
            isRunning = true;
            Console.WriteLine($"Server started on port {port}. Serving files from: {webRootPath}");
            Console.WriteLine("Listening for connections...");

            while (isRunning)
            {
                // AcceptTcpClient is a blocking call, waits for a client connection
                TcpClient client = listener.AcceptTcpClient();
                Console.WriteLine("Client connected.");

                // Handle the client connection in a new thread
                // Using a Thread Pool is more efficient for many small tasks,
                // but the requirement specifies creating a new Thread.
                Thread clientThread = new Thread(HandleClient);
                clientThread.Start(client);
            }
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"Socket Error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An unexpected error occurred: {ex.Message}");
        }
        finally
        {
            Stop(); // Ensure listener is stopped if the loop exits
        }
    }

    public void Stop()
    {
        if (isRunning)
        {
            isRunning = false;
            listener.Stop();
            Console.WriteLine("Server stopped.");
        }
    }

    private void HandleClient(object clientObject)
    {
        TcpClient client = (TcpClient)clientObject;
        NetworkStream stream = null;

        try
        {
            stream = client.GetStream();
            // Set a read timeout to prevent hanging indefinitely
            stream.ReadTimeout = 5000; // 5 seconds

            // Read the request
            string request = ReadRequest(stream);
            Console.WriteLine($"Received Request:\n{request}");

            if (string.IsNullOrEmpty(request))
            {
                Console.WriteLine("Received empty request.");
                SendErrorResponse(client, stream, 400, "Bad Request"); // Or simply close
                return; // Exit thread handling
            }

            // Parse the request line (e.g., "GET /index.html HTTP/1.1")
            string[] requestLines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string requestLine = requestLines[0];
            string[] requestParts = requestLine.Split(' ');

            if (requestParts.Length != 3)
            {
                Console.WriteLine($"Malformed request line: {requestLine}");
                SendErrorResponse(client, stream, 400, "Bad Request");
                return;
            }

            string method = requestParts[0];
            string requestedPath = requestParts[1];
            string httpVersion = requestParts[2];

            // 1. Check HTTP Method (Requirement)
            if (method != "GET")
            {
                Console.WriteLine($"Method Not Allowed: {method}");
                SendErrorResponse(client, stream, 405, "Method Not Allowed");
                return;
            }

            // 2. Sanitize and Validate Path (Requirement & Security)
            string safeRequestedPath = SanitizePath(requestedPath);
            if (safeRequestedPath == null)
            {
                Console.WriteLine($"Directory traversal attempt or invalid path: {requestedPath}");
                SendErrorResponse(client, stream, 400, "Bad Request"); // Or 403 Forbidden depending on policy
                return;
            }

            // Map root path "/" to "/index.html"
            if (safeRequestedPath == "/")
            {
                safeRequestedPath = "/index.html";
            }

            string filePath = Path.Combine(webRootPath, safeRequestedPath.Substring(1)); // Remove leading '/'

            // 3. Check File Extension (Requirement)
            string fileExtension = Path.GetExtension(filePath).ToLower();
            if (!mimeTypes.ContainsKey(fileExtension))
            {
                Console.WriteLine($"Forbidden file extension: {fileExtension}");
                SendErrorResponse(client, stream, 403, "Forbidden");
                return;
            }

            // 4. Check if File Exists and Serve (Requirement)
            if (File.Exists(filePath))
            {
                Console.WriteLine($"Serving file: {filePath}");
                ServeFile(stream, filePath, httpVersion, fileExtension);
            }
            else
            {
                Console.WriteLine($"File not found: {filePath}");
                SendErrorResponse(client, stream, 404, "Not Found");
            }
        }
        catch (IOException ioEx) when (ioEx.InnerException is SocketException)
        {
            // Handle cases where the client disconnects unexpectedly during read/write
            Console.WriteLine($"Client connection lost during communication: {ioEx.Message}");
        }
        catch (IOException ioEx)
        {
            // Handle other IO errors (e.g., file access issues)
            Console.WriteLine($"IO Error: {ioEx.Message}");
            try { SendErrorResponse(client, stream, 500, "Internal Server Error"); } catch { } // Attempt to send error
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred while handling client: {ex.Message}");
            // Attempt to send a generic server error response
            try { SendErrorResponse(client, stream, 500, "Internal Server Error"); } catch { }
        }
        finally
        {
            // Ensure the client connection is closed
            if (stream != null) stream.Close();
            if (client != null) client.Close();
            Console.WriteLine("Client disconnected.");
        }
    }

    private string ReadRequest(NetworkStream stream)
    {
        StringBuilder request = new StringBuilder();
        byte[] buffer = new byte[1024];
        int bytesRead = 0;
        bool sawNewLine = false; // Flag to track if we've seen \r\n
        bool sawDoubleNewLine = false; // Flag to track if we've seen \r\n\r\n

        try
        {
            // Read until the double newline indicating the end of headers
            // Or until timeout/client disconnect
            while (stream.DataAvailable || !sawDoubleNewLine)
            {
                bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0) break; // Client disconnected

                string chunk = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                request.Append(chunk);

                // Simple check for the end of headers (\r\n\r\n)
                // This is a basic check; a full HTTP parser is more complex.
                // We look for the last 4 characters being \r\n\r\n
                if (request.Length >= 4)
                {
                    if (request.ToString(request.Length - 4, 4) == "\r\n\r\n")
                    {
                        sawDoubleNewLine = true;
                        break; // Found end of headers
                    }
                }
                // Prevent infinite loop if DataAvailable is true but no more data comes (shouldn't happen with timeout)
                if (!stream.DataAvailable && !sawDoubleNewLine && bytesRead > 0)
                {
                    // Wait a tiny bit if no more data is immediately available
                    // This helps ensure we get the full header block
                    Thread.Sleep(1);
                }
            }
        }
        catch (IOException ex) // Catches ReadTimeoutException
        {
            Console.WriteLine($"Read timeout or error reading from stream: {ex.Message}");
            // Return partial request, HandleClient will likely treat this as malformed or bad request
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading request: {ex.Message}");
            // Return partial request
        }


        return request.ToString();
    }


    // Basic directory traversal prevention
    private string SanitizePath(string requestedPath)
    {
        // Decode URL encoded characters (like %20 for space)
        string decodedPath = Uri.UnescapeDataString(requestedPath);

        // Remove query string if present (e.g., "/index.html?query=...")
        int queryIndex = decodedPath.IndexOf('?');
        if (queryIndex >= 0)
        {
            decodedPath = decodedPath.Substring(0, queryIndex);
        }

        // Normalize path (handles slashes, etc.)
        string normalizedPath = decodedPath.Replace('/', Path.DirectorySeparatorChar);

        // Remove leading/trailing whitespace
        normalizedPath = normalizedPath.Trim();

        // Check for common traversal patterns: ".."
        if (normalizedPath.Contains(".."))
        {
            return null; // Indicate potential traversal attempt
        }

        // Construct the full intended path within the webroot
        string fullIntendedPath;
        try
        {
            // Combine web root with the requested path (treat requestedPath as relative to webroot)
            fullIntendedPath = Path.GetFullPath(Path.Combine(webRootPath, normalizedPath.TrimStart(Path.DirectorySeparatorChar)));
        }
        catch (Exception) // Handle potential issues with Path.Combine/GetFullPath
        {
            return null;
        }


        // Final check: Ensure the resulting path is actually inside the web root
        // Both paths must be in the same format (absolute, trailing separator etc.)
        // The comparison should be case-insensitive depending on OS, StartsWith handles this correctly by default on Windows.
        if (!fullIntendedPath.StartsWith(webRootPath, StringComparison.OrdinalIgnoreCase))
        {
            return null; // Path attempts to go outside webroot
        }

        // Return the path relative to webroot, starting with /
        // Rebuild the requested path format starting with /
        string relativeToWebRoot = fullIntendedPath.Substring(webRootPath.Length);
        return "/" + relativeToWebRoot.Replace(Path.DirectorySeparatorChar, '/'); // Return in URL format
    }


    private void ServeFile(NetworkStream stream, string filePath, string httpVersion, string fileExtension)
    {
        byte[] fileBytes = File.ReadAllBytes(filePath);
        string contentType = mimeTypes[fileExtension];

        string responseHeader = $"{httpVersion} 200 OK\r\n" +
                                $"Content-Type: {contentType}\r\n" +
                                $"Content-Length: {fileBytes.Length}\r\n" +
                                "Connection: close\r\n" + // Indicate that the server will close the connection
                                "\r\n"; // End of headers

        byte[] headerBytes = Encoding.ASCII.GetBytes(responseHeader);

        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(fileBytes, 0, fileBytes.Length); // Send file content
        stream.Flush();
    }

    private void SendErrorResponse(TcpClient client, NetworkStream stream, int statusCode, string statusMessage)
    {
        string body = $"<html><head><title>{statusCode} {statusMessage}</title></head><body><h1>Error {statusCode}: {statusMessage}</h1></body></html>";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body); // Use UTF8 for HTML body

        string responseHeader = $"HTTP/1.1 {statusCode} {statusMessage}\r\n" +
                                "Content-Type: text/html\r\n" +
                                $"Content-Length: {bodyBytes.Length}\r\n" +
                                "Connection: close\r\n" + // Indicate connection close
                                "\r\n";

        byte[] headerBytes = Encoding.ASCII.GetBytes(responseHeader);

        try
        {
            if (stream != null && stream.CanWrite) // Check if stream is usable
            {
                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Write(bodyBytes, 0, bodyBytes.Length);
                stream.Flush();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending error response {statusCode}: {ex.Message}");
            // At this point, the connection might be broken, just log and give up.
        }
    }
}