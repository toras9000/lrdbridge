using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;

namespace lrdbridge;

/// <summary>ブリッジ動作オプション</summary>
/// <param name="AcceptInterval">接続受け付け再試行インターバル[ms]</param>
/// <param name="BridgeTimeout">データブリッジのタイムアウト時間[ms]。この時間を超えて転送が完了しない場合、データの転送をあきらめるて破棄する。</param>
/// <param name="OutgoingCacheSize">出力。</param>
/// <param name="PauseWriterThreshold">ブリッジデータの書き込みを一時停止するバッファリング閾値[bytes]。</param>
/// <param name="SendBufferSize">TCP送信バッファサイズ[bytes]</param>
/// <param name="ReceiveBufferSize">TCP受信バッファサイズ[bytes]</param>
public record PumpTcpStreamBridgeOptions(
    int AcceptInterval = 1 * 1000,
    int BridgeTimeout = 3 * 1000,
    int OutgoingCacheSize = 4096,
    int PauseWriterThreshold = default,
    int SendBufferSize = default,
    int ReceiveBufferSize = default
)
{
    /// <summary><see cref="AcceptInterval"/> の最小値</summary>
    public const int MinimumAcceptInterval = 0;

    /// <summary><see cref="BridgeTimeout"/> の最小値</summary>
    public const int MinimumBridgeTimeout = 100;

    /// <summary><see cref="OutgoingCacheSize"/> の最小値</summary>
    public const int MinimumOutgoingCacheSize = 0;

    /// <summary><see cref="PauseWriterThreshold"/> の最小値</summary>
    public const int MinimumWriterThreshold = 1024;

    /// <summary><see cref="SendBufferSize"/> の最小値</summary>
    public const int MinimumSendBufferSize = 1024;

    /// <summary><see cref="ReceiveBufferSize"/> の最小値</summary>
    public const int MinimumReceiveBufferSize = 1024;

    /// <summary>プロパティ値を補正したインスタンスを作る</summary>
    /// <returns>補正されたインスタンス</returns>
    public PumpTcpStreamBridgeOptions Fixed()
    {
        var acceptInterval = Math.Max(this.AcceptInterval, MinimumAcceptInterval);
        var bridgeTimeout = Math.Max(this.BridgeTimeout, MinimumBridgeTimeout);
        var outgoingCache = Math.Max(this.OutgoingCacheSize, MinimumOutgoingCacheSize);
        var writerThreshold = this.PauseWriterThreshold;
        if (writerThreshold != default) { writerThreshold = Math.Max(writerThreshold, MinimumWriterThreshold); }
        var sendBufSize = this.SendBufferSize;
        if (sendBufSize != default) { sendBufSize = Math.Max(sendBufSize, MinimumSendBufferSize); }
        var recvBufSize = this.ReceiveBufferSize;
        if (recvBufSize != default) { recvBufSize = Math.Max(recvBufSize, MinimumReceiveBufferSize); }

        return new(
            AcceptInterval: acceptInterval,
            BridgeTimeout: bridgeTimeout,
            OutgoingCacheSize: outgoingCache,
            PauseWriterThreshold: writerThreshold,
            SendBufferSize: sendBufSize,
            ReceiveBufferSize: recvBufSize
        );
    }
}

/// <summary>TCP接続とストリームのブリッジ処理</summary>
/// <remarks>
/// TCPサーバとなり、接続されたクライアントとの送受信をストリームの形で行えるようにする。
/// クライアントとの接続が切断された場合もブリッジされたストリームは引き続き有効で、再接続されると新しいクライアントと送受信を行うことができる。
/// インスタンスを Dispose するとブリッジ処理を処理停止する。
/// </remarks>
public class PumpTcpStreamBridge : IAsyncDisposable
{
    // 構築
    #region コンストラクタ
    /// <summary>サービスエンドポイントを指定するコンストラクタ</summary>
    /// <param name="endpoint">TCP接続を受け付けるエンドポイント</param>
    /// <param name="options">ブリッジ動作オプション</param>
    public PumpTcpStreamBridge(IPEndPoint endpoint, PumpTcpStreamBridgeOptions? options = default)
    {
        // 利用するオプション設定を決定する
        this.bridgeOptions = options?.Fixed() ?? new();

        // インスタンスの設定を初期化
        this.AcceptInterval = this.bridgeOptions.AcceptInterval;
        this.BridgeTimeout = this.bridgeOptions.BridgeTimeout;

        // パイプオプションを決定
        var pipeOptions = (0 < this.bridgeOptions.PauseWriterThreshold) ? new(pauseWriterThreshold: this.bridgeOptions.PauseWriterThreshold) : PipeOptions.Default;

        // TCPクライアントからの受信データを読み取るパイプ及びストリーム
        this.incomingBridge = new Pipe(pipeOptions);
        this.Incoming = this.incomingBridge.Reader.AsStream();

        // TCPクライアントへの送信データデータを書き込むパイプ及びストリーム
        this.outgoingBridge = new Pipe(pipeOptions);
        this.Outgoing = this.outgoingBridge.Writer.AsStream();

        // 最終ソケットエラー
        this.LastSocketError = 0;

        // 処理停止用トークンソース
        this.disposer = new CancellationTokenSource();

        // TCPとストリームのブリッジ処理を開始
        this.bridgeTask = bridgeWork(endpoint);
    }
    #endregion

    // 公開プロパティ
    #region 設定
    /// <summary>接続受け付け再試行インターバル[ms]</summary>
    public int AcceptInterval { get; set; }

    /// <summary>データブリッジのタイムアウト時間[ms]</summary>
    /// <remarks>この時間を超えて転送が完了しない場合、データの転送をあきらめるて破棄する。</remarks>
    public int BridgeTimeout { get; set; }
    #endregion

    #region データ
    /// <summary>TCPクライアントからの受信データを読み出すストリーム</summary>
    public Stream Incoming { get; }

    /// <summary>TCPクライアントへの送信データを書き込むストリーム</summary>
    public Stream Outgoing { get; }
    #endregion

    #region 状態
    /// <summary>最終ソケットエラー</summary>
    public int LastSocketError { get; private set; }
    #endregion

    // 公開メソッド
    #region 破棄
    /// <summary>ブリッジ処理を停止してリソースを破棄する</summary>
    public async ValueTask DisposeAsync()
    {
        // Perform async cleanup.
        await DisposeAsyncCore();

        // Suppress finalization.
        GC.SuppressFinalize(this);
    }
    #endregion

    // 保護メソッド
    #region 破棄
    /// <summary>ブリッジ処理を停止してリソースを破棄する</summary>
    protected virtual async ValueTask DisposeAsyncCore()
    {
        // 破棄済みであれば何もせず
        // 非同期メソッドであるため、たとえ呼び出し元コンテキストが同一でも待機されない複数回の DisposeAsync 呼び出しがあると await 時の処理追い越しなどが生じうる。
        // そのため基本的に再入を考慮しておく必要があると思われる。
        var already = Interlocked.Exchange(ref this.disposed, 1);
        if (already != 0) return;

        // ブリッジタスクを停止する
        this.disposer.Cancel();
        try { await this.bridgeTask.ConfigureAwait(false); } catch { }

        // ブリッジパイプを完了させる
        await Task.WhenAll(
            this.incomingBridge.Reader.CompleteAsync().AsTask(),
            this.incomingBridge.Writer.CompleteAsync().AsTask(),
            this.outgoingBridge.Reader.CompleteAsync().AsTask(),
            this.outgoingBridge.Writer.CompleteAsync().AsTask(),
            Task.CompletedTask
        ).ConfigureAwait(false);

        // ブリッジストリームを破棄
        this.Incoming.Dispose();
        this.Outgoing.Dispose();

        // リソースを破棄
        this.disposer.Dispose();
        this.bridgeTask.Dispose();
    }
    #endregion

    // 非公開型
    #region リソース管理
    /// <summary>TCP接続のインスタンスを管理するコンテキスト情報型</summary>
    /// <remarks>TCPの接続・切断の状態変化に対処するため、利用する対象のTCPクライアントオブジェクトを保持・取り出しするためのものとなる。</remarks>
    private class RemoteContext
    {
        /// <summary>TCP接続の確立を通知するイベント</summary>
        public event Action? ConnectionEstablished;

        /// <summary>確立したTCP接続を保持する</summary>
        /// <param name="tcp">TCP接続</param>
        public void Established(TcpClient tcp)
        {
            this.remote = tcp;
            this.ConnectionEstablished?.Invoke();
        }

        /// <summary>保持しているTCP接続を除去する</summary>
        public void Disconnected()
        {
            this.remote = null;
        }

        /// <summary>保持しているTCP接続を取得する</summary>
        public TcpClient? GetRemote()
        {
            return this.remote;
        }

        /// <summary>保持しているTCP接続</summary>
        private TcpClient? remote;
    }
    #endregion

    // 非公開フィールド
    #region ブリッジ処理
    /// <summary>受信をブリッジするためのパイプ</summary>
    private Pipe incomingBridge;

    /// <summary>送信をブリッジするためのパイプ</summary>
    private Pipe outgoingBridge;

    /// <summary>ブリッジ処理タスク</summary>
    private Task bridgeTask;
    #endregion

    #region オプション
    /// <summary>ブリッジ動作のオプション設定</summary>
    private PumpTcpStreamBridgeOptions bridgeOptions;
    #endregion

    #region リソース管理
    /// <summary>dispose済みフラグ</summary>
    private int disposed;

    /// <summary>タスクを停止するトークンソース</summary>
    private CancellationTokenSource disposer;
    #endregion

    // 非公開メソッド
    #region ブリッジ処理
    /// <summary>ブリッジ処理を実行する。</summary>
    /// <param name="endpoint">TCP接続を受け付けるエンドポイント</param>
    private async Task bridgeWork(IPEndPoint endpoint)
    {
        // 受け付けた接続とのブリッジ処理を中止するトークンソース
        using var breaker = new CancellationTokenSource();

        // 今回の接続終了とインスタンス破棄の両方を中断要因とするリンクトークンを作成
        using var terminator = CancellationTokenSource.CreateLinkedTokenSource(this.disposer.Token, breaker.Token);

        // リモートとの接続状態を管理するコンテキストオブジェクト
        var context = new RemoteContext();

        // ストリームを読み出してリモートに転送する処理を開始
        // ストリームへの書き込みを消化するため、こちらはリモートの接続が無くても常時実行する
        using var outgoing = pumpRemoteOutgoing(context, this.disposer.Token);

        try
        {
            while (true)
            {
                // dispose済みの場合は終了する。
                if (this.disposer == null) return;

                try
                {
                    // TCP接続を受け付ける
                    using var remote = await acceptRemoteAsync(endpoint, this.disposer.Token).ConfigureAwait(false);

                    // オプション指定があればクライアントに設定
                    if (0 < this.bridgeOptions.SendBufferSize) remote.SendBufferSize = this.bridgeOptions.SendBufferSize;
                    if (0 < this.bridgeOptions.ReceiveBufferSize) remote.ReceiveBufferSize = this.bridgeOptions.ReceiveBufferSize;

                    // 接続できた場合はエラー値をクリア
                    this.LastSocketError = 0;

                    // 接続をコンテキストに紐づけ
                    context.Established(remote);

                    // 受信のブリッジ処理を開始
                    await pumpRemoteIncoming(context, this.disposer.Token);
                }
                catch (OperationCanceledException)
                {
                    // キャンセル例外は正常ルート。接続受付を脱してブリッジ終了に向かわせる。
                    throw;
                }
                catch (SocketException e)
                {
                    // エラーが一時的なものか恒久的なもの(エンドポイント値が正しくないなど)かはわからない。
                    // エラーのコード値を外部から参照可能にしておく。
                    this.LastSocketError = e.ErrorCode;
                }
                catch
                {
                    // Socket以外のエラーは適当な値にしておく。
                    this.LastSocketError = -1;
                }
                finally
                {
                    // 接続の紐づけを解除
                    context.Disconnected();
                }

                // エラー等でブリッジが停止した場合、再度接続の受付から繰り返す。
                // ただし、そもそもエンドポイントの指定が正しくなくて必ず失敗するといった状況も考えられるため、あまり処理を繰り返しすぎないようにするためのインターバル時間となる。
                await Task.Delay(this.AcceptInterval).ConfigureAwait(false);
            }
        }
        catch
        {
            // インスタンスの破棄時にはタスクがキャンセルによって中止される。これは正常な終了となる。
        }
        finally
        {
            // まだ終了していない側のブリッジ処理が最後の転送を終わらせることを想定した待機時間を設ける。
            // これは主にTCPクライアントが切断時に送信をシャットダウンして最後の受信を得ようとする動作を想定している。
            await Task.Delay(500).ConfigureAwait(false);

            // まだ停止していない処理を停止させる
            breaker.Cancel();
            try { await outgoing.ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>TCP接続を受け付ける。</summary>
    /// <param name="endpoint">TCP接続を受け付けるエンドポイント</param>
    /// <param name="cancelToken">キャンセルトークン</param>
    /// <returns>接続されたTCPクライアント</returns>
    private async ValueTask<TcpClient> acceptRemoteAsync(IPEndPoint endpoint, CancellationToken cancelToken)
    {
        var listener = new TcpListener(endpoint);
        try
        {
            listener.Start(1);
            return await listener.AcceptTcpClientAsync(cancelToken).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>TCPクライアントからの受信データをブリッジパイプに書き込む</summary>
    /// <param name="context">TCP接続のコンテキスト</param>
    /// <param name="cancelToken">キャンセルトークン</param>
    private async Task pumpRemoteIncoming(RemoteContext context, CancellationToken cancelToken)
    {
        // 接続情報取得
        var remote = context.GetRemote();
        if (remote == null) return;

        // Flushがブロックが続く場合にキャンセルするタイマー
        using var giveupTimer = new Timer(_ =>
        {
            this.incomingBridge.Writer.CancelPendingFlush();
        });

        var writer = this.incomingBridge.Writer;
        while (true)
        {
            // パイプへの書き込み用バッファ取得
            var buffer = writer.GetMemory();

            // TCPからの受信データをパイプのバッファに書き込み
            var recvSize = await remote.Client.ReceiveAsync(buffer, cancelToken).ConfigureAwait(false);

            // ストリーム終端ならば(切断された場合)終了
            if (recvSize == 0) break;

            // 書き込んだデータサイズをパイプに通知
            writer.Advance(recvSize);

            // タイムアウト時間以上かかるようであればキャンセルされるようにしてFlushする
            try
            {
                // ワンショットのキャンセルタイマーをかける
                giveupTimer.Change(this.BridgeTimeout, Timeout.Infinite);

                // フラッシュ実行。キャンセルされたかなどを判断する結果値が返されるが、それに関わる制御等は特にすることはない。
                await writer.FlushAsync(cancelToken);
            }
            finally
            {
                // タイマ停止。正常にFlush出来た場合はキャンセル処理が呼び出されないようにする。
                // ただし、タイマはプールスレッドから実行されるためFlushが正常完了しても入れ違いでキャンセルコールバックを実行してしまう場合はあり得る。
                // その場合は次のFlush呼び出しがキャンセルされてしまうがしかたない。
                giveupTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    /// <summary>ブリッジパイプからの読み取りデータをTCPクライアントに送信する</summary>
    /// <param name="context">TCP接続のコンテキスト</param>
    /// <param name="cancelToken">キャンセルトークン</param>
    private async Task pumpRemoteOutgoing(RemoteContext context, CancellationToken cancelToken)
    {
        var reader = this.outgoingBridge.Reader;
        var buffer = new RingBuffer(this.bridgeOptions.OutgoingCacheSize);

        // パイプの読み取りを中断するローカル関数
        void readBreaker() => reader.CancelPendingRead();

        // 接続確立イベントをハンドルして受信キャンセルさせる
        // また、スコープ離脱時のアンハンドルをリザーブする
        context.ConnectionEstablished += readBreaker;
        using var eventRemover = new DisposeAction(() => context.ConnectionEstablished -= readBreaker);

        while (true)
        {
            // パイプの読み取り可能データの取得
            var result = await reader.ReadAsync(cancelToken).ConfigureAwait(false);

            // 転送先のTCP接続を取得
            var remote = context.GetRemote();
            if (remote == null)
            {
                // 接続されていない場合はデータをリングバッファに蓄積する
                buffer.Accumulate(result.Buffer);
                reader.AdvanceTo(result.Buffer.End);
            }
            else
            {
                try
                {
                    // ブリッジをあきらめるためのトークンを作成
                    using var breaker = CancellationTokenSource.CreateLinkedTokenSource(this.disposer.Token);
                    breaker.CancelAfter(this.BridgeTimeout);

                    // バッファに蓄積されたデータがあればそれを先に送る
                    if (buffer.FirstMemory is var first && !first.IsEmpty) await remote.Client.SendAsync(first, breaker.Token).ConfigureAwait(false);
                    if (buffer.SecondMemory is var second && !second.IsEmpty) await remote.Client.SendAsync(second, breaker.Token).ConfigureAwait(false);
                    buffer.Clear();

                    // データをセグメントごとに送信する
                    if (!result.Buffer.IsEmpty)
                    {
                        var consumed = 0;
                        foreach (var segment in result.Buffer)
                        {
                            // セグメントデータを送信
                            var sendSize = await remote.Client.SendAsync(segment, breaker.Token).ConfigureAwait(false);
                            consumed += sendSize;
                            // 全量を送信できなかった場合は一旦終える
                            if (sendSize <= segment.Length) break;
                        }

                        // 送信できたサイズを超えるものはリングバッファに格納しておく
                        var afterData = result.Buffer.Slice(consumed);
                        buffer.Accumulate(afterData);
                    }
                }
                catch (OperationCanceledException) when (!this.disposer.IsCancellationRequested)
                {
                    // インスタンスの破棄じゃない場合(ブリッジを諦める場合)
                    // そのまま続ける。
                }

                // バッファ内容は消化する。
                reader.AdvanceTo(result.Buffer.End);
            }

            // パイプが完了済みあれば処理を終える。
            if (result.IsCompleted) break;
        }

    }
    #endregion

}
