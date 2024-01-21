namespace lrdbridgeTests;

[TestClass]
public class RingBufferTests
{
    [TestMethod]
    public void Accumulate()
    {
        // 初期状態
        var buffer = new RingBuffer(10);
        buffer.BufferSize.Should().Be(10);
        buffer.Length.Should().Be(0);
        buffer.FirstSpan.ToArray().Should().BeEmpty();
        buffer.SecondSpan.ToArray().Should().BeEmpty();

        // データ投入1 - 最初
        var drop1 = buffer.Accumulate(new([1, 2, 3]));
        drop1.Should().Be(0);
        buffer.Length.Should().Be(3);
        buffer.FirstSpan.ToArray().Should().Equal([1, 2, 3]);
        buffer.SecondSpan.ToArray().Should().Equal([]);

        // データ投入2 - 追加
        var drop2 = buffer.Accumulate(new([4, 5, 6, 7]));
        drop2.Should().Be(0);
        buffer.Length.Should().Be(7);
        buffer.FirstSpan.ToArray().Should().Equal([1, 2, 3, 4, 5, 6, 7]);
        buffer.SecondSpan.ToArray().Should().Equal([]);

        // データ投入3 - 折り返し
        var drop3 = buffer.Accumulate(new([8, 9, 10, 11, 12]));
        drop3.Should().Be(2);
        buffer.Length.Should().Be(10);
        buffer.FirstSpan.ToArray().Should().Equal([3, 4, 5, 6, 7, 8, 9, 10]);
        buffer.SecondSpan.ToArray().Should().Equal([11, 12]);

        // データ投入4 - 折り返し後の追加
        var drop4 = buffer.Accumulate(new([13, 14, 15, 16, 17]));
        drop4.Should().Be(5);
        buffer.Length.Should().Be(10);
        buffer.FirstSpan.ToArray().Should().Equal([8, 9, 10]);
        buffer.SecondSpan.ToArray().Should().Equal([11, 12, 13, 14, 15, 16, 17]);

        // データ投入5 - 折り返し後の折り返し
        var drop5 = buffer.Accumulate(new([18, 19, 20, 21, 22, 23, 24, 25, 26]));
        drop5.Should().Be(9);
        buffer.Length.Should().Be(10);
        buffer.FirstSpan.ToArray().Should().Equal([17, 18, 19, 20]);
        buffer.SecondSpan.ToArray().Should().Equal([21, 22, 23, 24, 25, 26]);

        // データ投入6 - サイズを超えるデータ
        var drop6 = buffer.Accumulate(new([27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38]));
        drop6.Should().Be(10);
        buffer.Length.Should().Be(10);
        buffer.FirstSpan.ToArray().Should().Equal([29, 30, 31, 32, 33, 34, 35, 36, 37, 38]);
        buffer.SecondSpan.ToArray().Should().Equal([]);

        // データ投入7 - サイズを超えるデータ2
        var drop7 = buffer.Accumulate(new([39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50]));
        drop7.Should().Be(10);
        buffer.Length.Should().Be(10);
        buffer.FirstSpan.ToArray().Should().Equal([41, 42, 43, 44, 45, 46, 47, 48, 49, 50]);
        buffer.SecondSpan.ToArray().Should().Equal([]);
    }

    [TestMethod]
    public void Consume()
    {
        // 最初のデータ投入
        var buffer = new RingBuffer(10);
        buffer.Accumulate(new([1, 2, 3, 4, 5, 6])).Should().Be(0);
        buffer.Length.Should().Be(6);
        buffer.FirstSpan.ToArray().Should().Equal([1, 2, 3, 4, 5, 6]);
        buffer.SecondSpan.ToArray().Should().Equal([]);

        // データ消費
        buffer.Consume(4).Should().Be(4);
        buffer.Length.Should().Be(2);
        buffer.FirstSpan.ToArray().Should().Equal([5, 6]);
        buffer.SecondSpan.ToArray().Should().Equal([]);

        // データ追加
        buffer.Accumulate(new([7, 8, 9, 10, 11, 12, 13])).Should().Be(0);
        buffer.Length.Should().Be(9);
        buffer.FirstSpan.ToArray().Should().Equal([5, 6, 7, 8, 9, 10]);
        buffer.SecondSpan.ToArray().Should().Equal([11, 12, 13]);

        // データ消費 (折り返し前)
        buffer.Consume(5).Should().Be(5);
        buffer.Length.Should().Be(4);
        buffer.FirstSpan.ToArray().Should().Equal([10]);
        buffer.SecondSpan.ToArray().Should().Equal([11, 12, 13]);

        // データ消費 (折り返しまたぎ)
        buffer.Consume(2).Should().Be(2);
        buffer.Length.Should().Be(2);
        buffer.FirstSpan.ToArray().Should().Equal([12, 13]);
        buffer.SecondSpan.ToArray().Should().Equal([]);

        // データ追加 (折り返しあり)
        buffer.Accumulate(new([14, 15, 16, 17, 18, 19, 20, 21])).Should().Be(0);
        buffer.Length.Should().Be(10);
        buffer.FirstSpan.ToArray().Should().Equal([12, 13, 14, 15, 16, 17, 18, 19, 20]);
        buffer.SecondSpan.ToArray().Should().Equal([21]);

        // データ消費 (ぴったり)
        buffer.Consume(10).Should().Be(10);
        buffer.Length.Should().Be(0);
        buffer.FirstSpan.ToArray().Should().Equal([]);
        buffer.SecondSpan.ToArray().Should().Equal([]);

    }

    [TestMethod]
    public void SizeZero()
    {
        // 初期状態
        var buffer = new RingBuffer(0);
        buffer.BufferSize.Should().Be(0);
        buffer.Length.Should().Be(0);
        buffer.FirstSpan.ToArray().Should().BeEmpty();
        buffer.SecondSpan.ToArray().Should().BeEmpty();

        // データ投入1
        var drop1 = buffer.Accumulate(new([1, 2, 3]));
        drop1.Should().Be(0);
        buffer.Length.Should().Be(0);
        buffer.FirstSpan.ToArray().Should().Equal([]);
        buffer.SecondSpan.ToArray().Should().Equal([]);

        // データ投入2
        var drop2 = buffer.Accumulate(new([4, 5, 6, 7]));
        drop2.Should().Be(0);
        buffer.Length.Should().Be(0);
        buffer.FirstSpan.ToArray().Should().Equal([]);
        buffer.SecondSpan.ToArray().Should().Equal([]);
    }
}
