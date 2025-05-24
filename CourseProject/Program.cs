using System;
using System.IO;

class Program
{
    static void Main(string[] args)
    {
        int port = 8080; // Default port
        // Get the path to the webroot folder relative to the executable
        string webRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webroot");

        // Ensure webroot exists (useful for deployment/running from bin/Debug)
        if (!Directory.Exists(webRoot))
        {
            try
            {
                Directory.CreateDirectory(webRoot);
                Console.WriteLine($"Created webroot directory at: {webRoot}");
                // Optionally, recreate default files if webroot was missing
                // This part could be more robust if needed
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error creating webroot directory: {ex.Message}");
                return; // Cannot proceed without webroot
            }
        }

        SimpleWebServer server = new SimpleWebServer(port, webRoot);

        // Handle Ctrl+C to stop the server gracefully
        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            Console.WriteLine("\nStopping server...");
            server.Stop();
            eventArgs.Cancel = true; // Prevent the process from terminating immediately
        };

        server.Start();

        Console.WriteLine("Server stopped. Press Enter to exit.");
        Console.ReadLine(); // Keep the console window open after server stops
    }
}