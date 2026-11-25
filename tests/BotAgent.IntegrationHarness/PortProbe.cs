using System.Net;
using System.Net.Sockets;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static void RunPortSelectionChecks()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.ExclusiveAddressUse = false;
        occupied.Start();
        var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
        Check("port selection rejects an occupied loopback listener", !CanBind(occupiedPort));
        var selected = FreePort(occupiedPort);
        Check("port selection replaces the occupied preferred port", selected != occupiedPort);

        using var control = new TcpListener(IPAddress.Any, selected);
        control.ExclusiveAddressUse = true;
        try
        {
            control.Start();
            Check("selected port supports the exclusive host binding", true);
        }
        catch (SocketException)
        {
            Check("selected port supports the exclusive host binding", false);
        }
    }
}
