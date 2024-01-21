using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace lrdbridgeTests;

[TestClass]
public class PumpTcpStreamBridgeTests
{
    [TestMethod()]
    public async Task Incoming_MultiRead_Each()
    {
        // TcpStreamBridge でサーバを実行する
        var endpoint = new IPEndPoint(IPAddress.Loopback, 9001);
        await using var bridge = new PumpTcpStreamBridge(endpoint);

        // 再受け入れインターバル
        bridge.AcceptInterval = 1 * 1000;

        // バッファ
        var buffer = new ArrayBufferWriter<byte>(1024);

        // クライアントを接続して送信テスト
        using (var tcp = new TcpClient())
        {
            // ブリッジサーバに接続
            await tcp.ConnectAsync(endpoint);

            // 適当なデータを送信
            await tcp.Client.SendAsync("abcdef".EncodeUtf8());
            await tcp.Client.SendAsync("ABCDEF".EncodeUtf8());

            // 入力ストリームにブリッジされたデータを取り出して検証
            var size = await bridge.Incoming.ReadToIdleAsync(buffer, idle: 500);
            buffer.WrittenMemory.ToArray().Should().Equal("abcdefABCDEF".EncodeUtf8());
        }

        // バッファクリア
        buffer.Clear();

        // 再受け入れ時間分待機する
        await Task.Delay(bridge.AcceptInterval);

        // クライアントを接続して送信テスト
        using (var tcp = new TcpClient())
        {
            // ブリッジサーバに接続
            await tcp.ConnectAsync(endpoint);

            // 適当なデータを送信
            await tcp.Client.SendAsync("vwxyz".EncodeUtf8());
            await tcp.Client.SendAsync("VWXYZ".EncodeUtf8());

            // 入力ストリームにブリッジされたデータを取り出して検証
            var size = await bridge.Incoming.ReadToIdleAsync(buffer, idle: 500);
            buffer.WrittenMemory.ToArray().Should().Equal("vwxyzVWXYZ".EncodeUtf8());
        }
    }

    [TestMethod()]
    public async Task Incoming_MultiRead_Union()
    {
        // TcpStreamBridge でサーバを実行する
        var endpoint = new IPEndPoint(IPAddress.Loopback, 9001);
        await using var bridge = new PumpTcpStreamBridge(endpoint);

        // 再受け入れインターバル
        bridge.AcceptInterval = 1 * 1000;

        // クライアントを接続して送信テスト
        using (var tcp = new TcpClient())
        {
            // ブリッジサーバに接続
            await tcp.ConnectAsync(endpoint);

            // 適当なデータを送信
            await tcp.Client.SendAsync("abcdef".EncodeUtf8());
            await tcp.Client.SendAsync("ABCDEF".EncodeUtf8());

            // 受け取り側での処理のために少し待つ
            await Task.Delay(500);
        }

        // 再受け入れ時間分待機する
        await Task.Delay(bridge.AcceptInterval);

        // クライアントを接続して送信テスト
        using (var tcp = new TcpClient())
        {
            // ブリッジサーバに接続
            await tcp.ConnectAsync(endpoint);

            // 適当なデータを送信
            await tcp.Client.SendAsync("vwxyz".EncodeUtf8());
            await tcp.Client.SendAsync("VWXYZ".EncodeUtf8());

            // 受け取り側での処理のために少し待つ
            await Task.Delay(500);
        }

        // 入力ストリームにブリッジされたデータを取り出して検証
        var buffer = new ArrayBufferWriter<byte>(1024);
        var size = await bridge.Incoming.ReadToIdleAsync(buffer, idle: 500);
        buffer.WrittenMemory.ToArray().Should().Equal("abcdefABCDEFvwxyzVWXYZ".EncodeUtf8());
    }

    [TestMethod()]
    public async Task Incoming_LargeData_Read()
    {
        // TcpStreamBridge でサーバを実行する
        var endpoint = new IPEndPoint(IPAddress.Loopback, 9001);
        var options = new PumpTcpStreamBridgeOptions(PauseWriterThreshold: 64 * 1024);
        await using var bridge = new PumpTcpStreamBridge(endpoint, options);

        // テスト向けのブリッジデータタイムアウトを設定
        bridge.BridgeTimeout = 500;

        // テスト用ダミーデータ
        var dummy = Random.Shared.GetBytes(options.PauseWriterThreshold);

        // クライアントを接続して送信テスト
        using (var tcp = new TcpClient())
        {
            // ブリッジサーバに接続
            await tcp.ConnectAsync(endpoint);

            // ストリームにブリッジされたデータを読み出すタスク
            using var breaker = new CancellationTokenSource();
            using var readTask = Task.Run(async () =>
            {
                var buffer = new ArrayBufferWriter<byte>(10 * dummy.Length);
                try
                {
                    while (true)
                    {
                        var memory = buffer.GetMemory();
                        var size = await bridge.Incoming.ReadAsync(memory, breaker.Token);
                        buffer.Advance(size);
                    }
                }
                catch (OperationCanceledException) { }

                return buffer;
            });

            // 適当なデータを送信
            var count = 10;
            for (var i = 0; i < count; i++)
            {
                await tcp.Client.SendAsync(dummy);
            }

            // ブリッジされたデータの読み出しが完了するのを待機する
            await Task.Delay(bridge.BridgeTimeout * 2);

            // 読み取り処理を中止して結果を得る。
            breaker.Cancel();
            var received = await readTask;

            // 読み取り結果の検証
            var verify = received.WrittenMemory;
            verify.Length.Should().Be(dummy.Length * count);
            while (!verify.IsEmpty)
            {
                var expectLen = Math.Min(dummy.Length, verify.Length);
                verify[..expectLen].Span.SequenceEqual(dummy.AsSpan(0, expectLen)).Should().BeTrue();
                verify = verify[expectLen..];
            }
        }
    }

    [TestMethod()]
    public async Task Outgoing_MultiWrite_Each()
    {
        // TcpStreamBridge でサーバを実行する
        var endpoint = new IPEndPoint(IPAddress.Loopback, 9001);
        await using var bridge = new PumpTcpStreamBridge(endpoint);

        // 再受け入れインターバル
        bridge.AcceptInterval = 1 * 1000;

        // バッファ
        var buffer = new ArrayBufferWriter<byte>(1024);

        // クライアントを接続して送信テスト
        using (var tcp = new TcpClient())
        {
            // ブリッジサーバに接続
            await tcp.ConnectAsync(endpoint);

            // 確立した接続がブリッジ処理での転送先として準備される時間が必要なので待つ。
            await Task.Delay(500);

            // 適当なデータをストリームに書き込み
            await bridge.Outgoing.WriteAsync("abcdef".EncodeUtf8());
            await bridge.Outgoing.WriteAsync("ABCDEF".EncodeUtf8());

            // 出力ストリームからブリッジされたTCP受信データを検証
            using var stream = tcp.GetStream();
            var size = await stream.ReadToIdleAsync(buffer, idle: 500);
            buffer.WrittenMemory.ToArray().Should().Equal("abcdefABCDEF".EncodeUtf8());
        }

        // バッファクリア
        buffer.Clear();

        // 再受け入れ時間分待機する
        await Task.Delay(bridge.AcceptInterval);

        // クライアントを接続して送信テスト
        using (var tcp = new TcpClient())
        {
            // ブリッジサーバに接続
            await tcp.ConnectAsync(endpoint);

            // 確立した接続がブリッジ処理での転送先として準備される時間が必要なので待つ。
            await Task.Delay(500);

            // 適当なデータをストリームに書き込み
            await bridge.Outgoing.WriteAsync("vwxyz".EncodeUtf8());
            await bridge.Outgoing.WriteAsync("VWXYZ".EncodeUtf8());

            // 出力ストリームからブリッジされたTCP受信データを検証
            using var stream = tcp.GetStream();
            var size = await stream.ReadToIdleAsync(buffer, idle: 500);
            buffer.WrittenMemory.ToArray().Should().Equal("vwxyzVWXYZ".EncodeUtf8());
        }
    }

    [TestMethod()]
    public async Task Outgoing_Unconnected()
    {
        // TcpStreamBridge でサーバを実行する
        var endpoint = new IPEndPoint(IPAddress.Loopback, 9001);
        await using var bridge = new PumpTcpStreamBridge(endpoint);

        // 再受け入れインターバル
        bridge.AcceptInterval = 1 * 1000;

        // 未接続状態でのデータ書き込み

        // 適当なデータをストリームに書き込み
        await bridge.Outgoing.WriteAsync("abcdef".EncodeUtf8());
        await bridge.Outgoing.WriteAsync("ABCDEF".EncodeUtf8());

        // ブリッジ動作処理が空転するのをちょっと待つ。
        await Task.Delay(500);

        // 適当なデータをストリームに書き込み
        await bridge.Outgoing.WriteAsync("vwxyz".EncodeUtf8());
        await bridge.Outgoing.WriteAsync("VWXYZ".EncodeUtf8());

        // ブリッジ動作処理が空転するのをちょっと待つ。
        await Task.Delay(500);

        // クライアントを接続して送信テスト
        using (var tcp = new TcpClient())
        {
            // ブリッジサーバに接続
            await tcp.ConnectAsync(endpoint);

            // 確立した接続がブリッジ処理での転送先として準備される時間が必要なので待つ。
            await Task.Delay(500);

            // バッファ
            var buffer = new ArrayBufferWriter<byte>(1024);

            // 出力ストリームからブリッジされたTCP受信データを検証
            // 未接続状態でのデータは一定量バッファリングされ、接続時に受信できるはず。
            using var stream = tcp.GetStream();
            var size = await stream.ReadToIdleAsync(buffer, idle: 500);
            buffer.WrittenMemory.ToArray().Should().Equal("abcdefABCDEFvwxyzVWXYZ".EncodeUtf8());
        }
    }

    [TestMethod()]
    public async Task Outgoing_LargeData_Unconnected()
    {
        // TcpStreamBridge でサーバを実行する
        var endpoint = new IPEndPoint(IPAddress.Loopback, 9001);
        var options = new PumpTcpStreamBridgeOptions(OutgoingCacheSize: 1024);
        await using var bridge = new PumpTcpStreamBridge(endpoint, options);

        // 再受け入れインターバル
        bridge.AcceptInterval = 1 * 1000;

        // テスト用ダミーデータ
        var dummy = Random.Shared.GetBytes(1024);

        // 未接続状態で適当なデータを送信
        var count = 10;
        for (var i = 0; i < count; i++)
        {
            await bridge.Outgoing.WriteAsync(dummy);
        }

        // ブリッジ動作処理が空転するのをちょっと待つ。
        await Task.Delay(500);

        // クライアントを接続して送信テスト
        using (var tcp = new TcpClient())
        {
            // ブリッジサーバに接続
            await tcp.ConnectAsync(endpoint);

            // 確立した接続がブリッジ処理での転送先として準備される時間が必要なので待つ。
            await Task.Delay(500);

            // バッファ
            var buffer = new ArrayBufferWriter<byte>(count * dummy.Length);

            // 出力ストリームからブリッジされたTCP受信データを検証
            // 未接続状態でのデータは一定量バッファリングされ、接続時に受信できるはず。
            using var stream = tcp.GetStream();
            var size = await stream.ReadToIdleAsync(buffer, idle: 500);
            buffer.WrittenMemory.ToArray().Should().Equal(dummy.Repetition(10).Take(options.OutgoingCacheSize));
        }
    }
}
