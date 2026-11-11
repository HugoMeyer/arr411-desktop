using System.Net;
using System.Net.Sockets;

namespace Arr411.E2E;

public static class Net
{
    /// <summary>A loopback port nobody listens on (for a moment).</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
