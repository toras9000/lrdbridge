using System.Buffers;

namespace lrdbridge;

/// <summary>
/// リングバッファ
/// </summary>
internal class RingBuffer
{
    // 構築
    #region コンストラクタ
    /// <summary>バッファサイズを指定するコンストラクタ</summary>
    /// <param name="bufferSize">バッファサイズ[bytes]</param>
    public RingBuffer(int bufferSize)
    {
        this.buffer = new byte[bufferSize];
        this.offset = 0;
        this.length = 0;
    }
    #endregion

    // 公開プロパティ
    #region バッファ情報
    /// <summary>バッファサイズ</summary>
    public int BufferSize => this.buffer.Length;

    /// <summary>蓄積されたデータ量</summary>
    public int Length => this.length;
    #endregion

    #region バッファ内容
    /// <summary>バッファ内容の前方側データブロック</summary>
    public ReadOnlySpan<byte> FirstSpan
    {
        get
        {
            var behind = this.buffer.Length - this.offset;
            var available = Math.Min(this.length, behind);
            return this.buffer.AsSpan(this.offset, available);
        }
    }

    /// <summary>バッファ内容の前方側データブロック</summary>
    public ReadOnlyMemory<byte> FirstMemory
    {
        get
        {
            var behind = this.buffer.Length - this.offset;
            var available = Math.Min(this.length, behind);
            return this.buffer.AsMemory(this.offset, available);
        }
    }

    /// <summary>バッファ内容の後方側データブロック</summary>
    public ReadOnlySpan<byte> SecondSpan
    {
        get
        {
            var behind = this.buffer.Length - this.offset;
            if (this.length <= behind) return ReadOnlySpan<byte>.Empty;
            var flap = this.length - behind;
            return this.buffer.AsSpan(0, flap);
        }
    }

    /// <summary>バッファ内容の後方側データブロック</summary>
    public ReadOnlyMemory<byte> SecondMemory
    {
        get
        {
            var behind = this.buffer.Length - this.offset;
            if (this.length <= behind) return ReadOnlyMemory<byte>.Empty;
            var flap = this.length - behind;
            return this.buffer.AsMemory(0, flap);
        }
    }
    #endregion

    // 公開メソッド
    #region バッファ変更
    /// <summary>データをバッファに追加する</summary>
    /// <param name="data">追加するデータ</param>
    /// <returns>捨てたデータのサイズ[bytes]</returns>
    public int Accumulate(ReadOnlySequence<byte> data)
    {
        // int表現範囲外のサイズはサポート外
        if (int.MaxValue < data.Length) throw new NotSupportedException();

        // バッファサイズとデータサイズの比較
        if (this.buffer.Length <= data.Length)
        {
            // バッファサイズを超える場合は最後の部分だけを保持
            var drop = this.length;
            var skip = data.Length - this.buffer.Length;
            data.Slice(skip).CopyTo(this.buffer);
            this.offset = 0;
            this.length = this.buffer.Length;
            return drop;
        }
        else
        {
            // バッファサイズのデータと追加データを連結する場合

            // 合わせてデータがあふれる場合はその分を消化しておく
            var drop = 0;
            var space = this.buffer.Length - this.Length;
            if (space < data.Length)
            {
                drop = (int)(data.Length - space);
                this.Consume(drop);
            }

            // データをバッファに追加する
            var behind = this.buffer.Length - this.offset;
            if (behind <= this.length)
            {
                // 既存データが折り返している場合は単に追加。
                // 先にあふれる分を消化しているため、ここで再度折り返す(既存データにかかる)ことはない。
                var flaptail = this.length - behind;
                data.CopyTo(this.buffer.AsSpan(flaptail));
            }
            else
            {
                // 既存データの後ろへの追加と、折り返してバッファ先頭からの追加
                // 折り返したサイズはゼロの場合もありうる。
                var tailspace = behind - this.length;
                var tailspan = this.buffer.AsSpan()[^tailspace..];
                var firstpart = Math.Min(data.Length, tailspace);
                data.Slice(0, firstpart).CopyTo(tailspan);
                data.Slice(firstpart).CopyTo(this.buffer);
            }
            this.length += (int)data.Length;

            return drop;
        }
    }

    /// <summary>バッファ内容を消化する</summary>
    /// <param name="size">消化するサイズ[bytes]</param>
    /// <returns>捨てたデータのサイズ[bytes]</returns>
    public int Consume(int size)
    {
        // ゼロ以下の場合は変化なし
        if (size <= 0) return 0;

        // 全データ消化する場合はシンプルにクリア
        if (this.length <= size)
        {
            var drop = this.length;
            this.offset = 0;
            this.length = 0;
            return drop;
        }

        // 指定されたサイズ分を切り詰め
        var behind = this.buffer.Length - this.offset;
        if (behind < size)
        {
            this.offset = size - behind;
        }
        else
        {
            this.offset += size;
        }
        this.length -= size;

        return size;
    }

    /// <summary>バッファ内容をクリア</summary>
    public void Clear()
        => this.Consume(this.buffer.Length);
    #endregion

    // 非公開フィールド
    #region バッファ管理
    /// <summary>バッファ</summary>
    private byte[] buffer;
    private int offset;
    private int length;
    #endregion
}
