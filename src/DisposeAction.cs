namespace lrdbridge;

/// <summary>
/// Dispose時のアクションをラップするクラス
/// </summary>
internal class DisposeAction : IDisposable
{
    /// <summary>Dispose時の処理を指定するコンストラクタ</summary>
    /// <param name="action">Dispose時に実行する処理</param>
    public DisposeAction(Action action)
    {
        this.action = action;
    }

    /// <summary>予約されたアクションを実行する</summary>
    public void Dispose()
    {
        this.action?.Invoke();
        this.action = default!;
    }

    /// <summary>破棄時に実行するアクション</summary>
    private Action action;
}
