using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using OfflineChatter.Core.Net;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class LoopbackForwarderTests
{
    [Fact]
    public async Task Forwards_Ipv6_Loopback_To_Ipv4_Server()
    {
        if (!Socket.OSSupportsIPv6) return;
        // «Сервер» только на 127.0.0.1, как llama-server
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var c = await server.AcceptTcpClientAsync();
            var s = c.GetStream();
            var buf = new byte[4];
            var read = 0;
            while (read < 4) read += await s.ReadAsync(buf, read, 4 - read);
            var reply = Encoding.ASCII.GetBytes("pong:" + Encoding.ASCII.GetString(buf));
            await s.WriteAsync(reply, 0, reply.Length);
        });

        using var fwd = new LoopbackForwarder(port);
        Assert.True(fwd.Start());

        using var client = new TcpClient(AddressFamily.InterNetworkV6);
        try
        {
            await client.ConnectAsync(IPAddress.IPv6Loopback, port);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            // Песочница или политика запрещают IPv6-петлю — проверить пересылку здесь нельзя
            server.Stop();
            return;
        }
        var stream = client.GetStream();
        var ping = Encoding.ASCII.GetBytes("ping");
        await stream.WriteAsync(ping, 0, ping.Length);
        var answer = new byte[9];
        var got = 0;
        while (got < answer.Length)
        {
            var n = await stream.ReadAsync(answer, got, answer.Length - got);
            if (n == 0) break;
            got += n;
        }
        await serverTask;
        server.Stop();
        Assert.Equal("pong:ping", Encoding.ASCII.GetString(answer, 0, got));
    }
}
