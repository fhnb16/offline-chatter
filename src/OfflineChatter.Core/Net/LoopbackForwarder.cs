using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OfflineChatter.Core.Diagnostics;

namespace OfflineChatter.Core.Net;

/// <summary>
/// Пересылка соединений с IPv6-петли [::1]:порт на 127.0.0.1:порт.
/// llama-server слушает один адрес (127.0.0.1), а «localhost» в Windows разрешается сначала в ::1 —
/// многие клиенты (Node.js, Electron-приложения) пробуют только его и получают «ошибку сети».
/// Слушает только петлю: доступ из сети это не открывает.
/// </summary>
public sealed class LoopbackForwarder : IDisposable
{
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    public LoopbackForwarder(int port) => _port = port;

    public bool IsRunning => _listener != null;

    /// <summary>Начать слушать [::1]:порт. false — IPv6 отключён или порт занят (тогда работает только 127.0.0.1).</summary>
    public bool Start()
    {
        if (!Socket.OSSupportsIPv6) return false;
        try
        {
            var listener = new TcpListener(IPAddress.IPv6Loopback, _port);
            listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
            listener.Start();
            _listener = listener;
            _ = Task.Run(AcceptLoopAsync);
            Log.Info("Net", "IPv6 loopback forwarder started", new { port = _port });
            return true;
        }
        catch (SocketException ex)
        {
            Log.Warn("Net", "IPv6 loopback forwarder not started", ex);
            return false;
        }
    }

    private async Task AcceptLoopAsync()
    {
        var listener = _listener!;
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => PipeAsync(client));
        }
    }

    private async Task PipeAsync(TcpClient client)
    {
        using (client)
        using (var upstream = new TcpClient(AddressFamily.InterNetwork))
        {
            try
            {
                client.NoDelay = true;
                upstream.NoDelay = true;
                await upstream.ConnectAsync(IPAddress.Loopback, _port).ConfigureAwait(false);
                var a = client.GetStream();
                var b = upstream.GetStream();
                // Ответы llama-server потоковые (SSE): копируем в обе стороны, пока одна из них не закроется
                var t1 = CopyAsync(a, b);
                var t2 = CopyAsync(b, a);
                await Task.WhenAny(t1, t2).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or System.IO.IOException or ObjectDisposedException)
            {
                // сервер перезапускается или клиент отключился — соединение просто закрываем
            }
        }
    }

    private async Task CopyAsync(System.IO.Stream from, System.IO.Stream to)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer, 0, buffer.Length, _cts.Token).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer, 0, n, _cts.Token).ConfigureAwait(false);
                await to.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or ObjectDisposedException or OperationCanceledException or SocketException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
    }
}
